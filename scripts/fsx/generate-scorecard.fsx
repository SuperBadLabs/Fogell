#load "prelude.fsx"
/// FG-090/091/092. Generates the compatibility scorecard, the machine-readable
/// ledger, and KNOWN-LIMITATIONS.md — from evidence on disk, never by hand.
///
/// ADR 0001 fixes three tiers and forbids collapsing them into one number, with the
/// reason recorded: a prior engine had 146 non-empty IRs and 5 files of proven
/// parity, and a single percentage would have implied 64%.
///
/// THE SAME TRAP IS LIVE IN THIS REPO. Nearly every differential receipt is for a
/// hand-written case in `differential/cases`, not for a corpus Jenkinsfile — the
/// name overlap between the two populations is the handful of corpus receipts the
/// ledger's tier-1 rows name. Printing a case count anywhere near "228 files"
/// invites dividing one by the other, and the corpus figure for proven parity is
/// the tier-1 count alone. The two populations are therefore reported in SEPARATE
/// SECTIONS, each with its own denominator stated on the same line as its count,
/// and no ratio is ever computed.
///
/// Parse only. Corpus files are untrusted third-party CI code: this reads and parses
/// them and never executes one.
///
///   usage: scripts/bin/generate-scorecard [--check]
///          --check regenerates IN MEMORY and fails if the committed artifacts
///          differ from what the current evidence produces. It writes nothing.
///
///          IT DOES NOT PROTECT EVERY HOST. `build-and-test.sh` skips this check
///          when `FOGELL_CORPUS` is absent, and CI excludes corpus work by design,
///          so a stale artifact passes on GitHub. Drift is caught on luigi/HeMan only.
///
/// Ported from `generate-scorecard.bb` under FG-226. ONE BEHAVIOURAL DEVIATION,
/// stated here because it reaches a PUBLISHED artifact: the babashka original
/// ordered equal-count rows in `group-by` hash-bucket order, so the tier-3 code
/// table and the KNOWN-LIMITATIONS reason sections were ordered by an accident of
/// Clojure's hash implementation. Ties are now broken by the code and the reason
/// text respectively. Counts and membership are unchanged; only the relative order
/// of equal-count entries moves, and it is now stable across hosts and versions
/// rather than depending on a map implementation nothing pins.
open System
open System.IO
open System.Text.RegularExpressions
open Prelude

type Tier =
    | Tier1
    | Tier3
    | Admitted

type Row =
    { File: string
      Verdict: string
      Code: string
      Stages: string
      Steps: string
      Detail: string }

type ReceiptState =
    | Proven
    | WrongCore
    | Other

let nth (a: string[]) i = if i < a.Length then a.[i] else null
let orEmpty (s: string) = if isNull s then "" else s

[<EntryPoint>]
let main argv =
    let check = argv |> Array.contains "--check"
    // See audit-board-numbers for why this is three parents rather than two.
    let exeDir = Path.GetDirectoryName(Environment.ProcessPath)
    let root = Path.GetFullPath(Path.Combine(exeDir, "..", ".."))
    let corpus =
        match Environment.GetEnvironmentVariable "FOGELL_CORPUS" with
        | null | "" -> "/sn8100/work/exchange/crucible-gate/corpus"
        | v -> v
    let expectedCore =
        match Environment.GetEnvironmentVariable "FOGELL_JENKINS_CORE" with
        | null | "" -> "2.568.1"
        | v -> v

    // THE CORPUS GATE RUNS FIRST, as its own header demands. A drifted corpus
    // invalidates every count below it, so a scorecard generated over one is
    // worse than none — it looks authoritative and is not.
    let gate = runIn root [] "scripts/verify-corpus.sh" []
    if gate.Rc <> 0 then
        out "FAIL: corpus gate refused; no scorecard generated"
        out (javaTrim (gate.Out + gate.Err))
        exitWith 1

    let score =
        runIn root [] "dotnet"
            [ "run"; "--project"; "tools/Fogell.Corpus.Score"; "-c"; "Release"; "--no-build"; "--"; corpus ]
    if score.Rc <> 0 then
        out "FAIL: corpus scorer did not run"
        out (javaTrim score.Err)
        exitWith 1

    let tabRx = Regex("\t")
    let rows =
        splitLines score.Out
        |> List.skip 1
        |> List.filter (fun l -> not (blank l))
        |> List.map (fun l ->
            let p = tabRx.Split l
            { File = nth p 0
              Verdict = nth p 1
              Code = nth p 2
              Stages = nth p 3
              Steps = nth p 4
              Detail = nth p 5 })

    // MIRRORS THE WRITER, deliberately. `Compare.fs` builds the receipt name as
    // `r.File.Replace("/", "_").Replace(".Jenkinsfile", "")` — a GLOBAL replace —
    // so `foo.Jenkinsfile.Jenkinsfile` becomes `foo.receipt.txt`. An ANCHORED
    // regex would make the reader disagree with the writer for exactly that name.
    let stemOf (n: string) = n.Replace("/", "_").Replace(".Jenkinsfile", "")

    // THE EXPECTED RECEIPT SET, DERIVED FROM THE CASES THEMSELVES. A case
    // containing `//// NEXT BUILD ////` separators is a SEQUENCE and emits
    // `<case>.b1` … `.bN` for N = separators + 1. Deriving the set asks what the
    // cases PRODUCE rather than what a receipt name RESEMBLES.
    let nextBuildRx = javaRx "(?m)^//// NEXT BUILD ////\\s*$"
    let expectedMappings =
        glob (Path.Combine(root, "differential/cases")) "*.Jenkinsfile"
        |> List.collect (fun f ->
            let stem = stemOf (Path.GetFileName f)
            let builds = 1 + nextBuildRx.Matches(slurp f).Count
            if builds = 1 then [ (stem, f) ]
            else [ for b in 1 .. builds -> (stem + ".b" + string b, f) ])

    // A COLLISION IS AN ERROR, NOT A DEDUPLICATION. `foo.Jenkinsfile` as a
    // sequence synthesises `foo.b1`, and a separate case `foo.b1.Jenkinsfile`
    // expects the same name; a set silently collapsed two expected builds into one.
    let dups =
        expectedMappings
        |> List.countBy fst
        |> List.filter (fun (_, n) -> n > 1)
        |> List.map fst
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
    if not (List.isEmpty dups) then
        out ("FAIL: two cases expect the same receipt name: " + String.Join(", ", dups))
        exitWith 1

    // Construct the lookup only AFTER proving names unique.
    let expectedCaseByReceipt = expectedMappings |> List.fold (fun (m: Map<string, string>) (r, c) -> m.Add(r, c)) Map.empty
    let expectedReceipts = expectedMappings |> List.map fst |> Set.ofList

    // A RECEIPT NAMING A CORPUS FILE IS TIER-1 EVIDENCE and must survive this
    // filter. An orphan filter once discarded it before the tier lookup, closing
    // the tier-1 path FG-090 exists to open.
    let corpusStems = rows |> List.map (fun r -> stemOf r.File) |> Set.ofList

    let receiptSuffixRx = javaRx "\\.receipt\\.txt$"
    let coreRx = javaRx "(?m)^jenkins-core:\\s*(\\S+)"
    // THE TIER-1 VERDICT FIELD, matched to its END. `PROVEN-PARTIAL` also starts
    // with "PROVEN" and explicitly is not tier 1; a prefix match would count it.
    let tier1Rx = javaRx "(?m)^VERDICT: PROVEN \\(tier 1\\)(?:\\s+—[^\n]*)?$"
    let receipts =
        glob (Path.Combine(root, "differential/receipts")) "*.receipt.txt"
        |> List.choose (fun f ->
            let n = receiptSuffixRx.Replace(Path.GetFileName f, "")
            if not (expectedReceipts.Contains n || corpusStems.Contains n) then None
            else
                let body = slurp f
                let coreM = coreRx.Match body
                let coreLine = if coreM.Success then coreM.Groups.[1].Value else null
                let state =
                    if not (tier1Rx.IsMatch body) then Other
                    elif coreLine <> expectedCore then WrongCore
                    else Proven
                Some(n, state))
        |> List.fold (fun (m: Map<string, ReceiptState>) (k, v) -> m.Add(k, v)) Map.empty

    // REJECTION WINS OVER AN OLD RECEIPT. A receipt proves what the engine did
    // when it ran; if the CURRENT engine cannot parse the file, "proven
    // compatible" is a claim about a binary that no longer exists.
    let tierOf (r: Row) =
        let stem = orEmpty(r.File).Replace(".Jenkinsfile", "")
        if r.Verdict = "err" || r.Verdict = "scripted-err" then Tier3
        elif receipts.TryFind stem = Some Proven then Tier1
        else Admitted

    let ledger =
        rows
        |> List.map (fun r ->
            let t = tierOf r
            let evidence =
                match t with
                | Tier1 -> "receipt:" + orEmpty(r.File).Replace(".Jenkinsfile", "")
                | Tier3 -> orEmpty r.Code + " " + orEmpty r.Detail
                | Admitted -> "parsed; execution NOT attempted (untrusted corpus) — not ADR tier 2"
            (r, t, evidence))
        |> List.sortWith (fun (a, _, _) (b, _, _) -> String.CompareOrdinal(orEmpty a.File, orEmpty b.File))

    let tier3Rows = ledger |> List.filter (fun (_, t, _) -> t = Tier3)
    let t1 = ledger |> List.filter (fun (_, t, _) -> t = Tier1) |> List.length
    let t2 = ledger |> List.filter (fun (_, t, _) -> t = Admitted) |> List.length
    let t3 = List.length tier3Rows
    let total = List.length ledger

    let byCode =
        tier3Rows
        |> List.countBy (fun (r, _, _) -> orEmpty r.Code)
        |> List.sortWith (fun (ca, na) (cb, nb) ->
            match compare nb na with
            | 0 -> String.CompareOrdinal(ca, cb)
            | c -> c)

    let caseExpected = Set.count expectedReceipts
    let casePresent = receipts |> Map.filter (fun k _ -> expectedReceipts.Contains k) |> Map.count
    let caseMissing = caseExpected - casePresent
    let caseProven =
        receipts |> Map.filter (fun k v -> expectedReceipts.Contains k && v = Proven) |> Map.count

    // MTIME IS ENVIRONMENT STATE AND NEVER ENTERS THE DOCUMENT. A fresh checkout
    // gives arbitrary mtimes, so interpolating this into a byte-compared artifact
    // would make `--check` fail on a clean clone.
    let staleReceipts =
        glob (Path.Combine(root, "differential/receipts")) "*.receipt.txt"
        |> List.choose (fun f ->
            let n = receiptSuffixRx.Replace(Path.GetFileName f, "")
            match expectedCaseByReceipt.TryFind n with
            | Some caseFile when File.GetLastWriteTimeUtc caseFile > File.GetLastWriteTimeUtc f -> Some n
            | _ -> None)
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    let tierToken t = match t with | Tier1 -> "1" | Tier3 -> "3" | Admitted -> "admitted"
    let ledgerTsv =
        "# Compatibility ledger — generated by scripts/bin/generate-scorecard (build it with scripts/build-audits.sh); do not edit\n"
        + "# tier 1 = proven compatible (differential receipt names this file)\n"
        + "# admitted = parses; execution NOT attempted (untrusted corpus). NOT ADR tier 2,\n"
        + "#            which requires parsing AND executing — that is NOT ASSESSED here.\n"
        + "# tier 3 = rejected (named error code and source position)\n"
        + "file\ttier\tcode\tevidence\n"
        + String.Join("\n",
            ledger |> List.map (fun (r, t, ev) ->
                orEmpty r.File + "\t" + tierToken t + "\t"
                + (if blank r.Code then "-" else r.Code) + "\t" + ev))
        + "\n"

    // The rejection REASON, not just the code. All tier-3 files carry the same
    // `malformed_syntax` code, which names nothing a reader can act on; the parser
    // message behind it does.
    let atPosRx = javaRx "\\s*@\\d+:\\d+\\s*$"
    let reasonOf (r: Row) =
        let detail = orEmpty r.Detail
        let code = orEmpty r.Code
        let stripped =
            let prefix = code + " "
            if code <> "" && detail.StartsWith(prefix, StringComparison.Ordinal)
            then detail.Substring prefix.Length
            else detail
        javaTrim (atPosRx.Replace(stripped, ""))

    let byReason =
        tier3Rows
        |> List.groupBy (fun (r, _, _) -> reasonOf r)
        |> List.map (fun (reason, xs) ->
            let display = if blank reason then "(no message)" else reason
            let examples =
                xs
                |> List.map (fun (r, _, _) -> orEmpty r.File)
                |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                |> List.truncate 3
            (display, List.length xs, examples))
        |> List.sortWith (fun (ra, na, _) (rb, nb, _) ->
            match compare nb na with
            | 0 -> String.CompareOrdinal(ra, rb)
            | c -> c)

    let limitationsMd =
        "# Known limitations\n\n"
        + "Generated by `scripts/bin/generate-scorecard` (build it with `scripts/build-audits.sh`) from the compatibility ledger. Do not edit.\n\n"
        + "Refusals GROUPED BY the parser's own message, ranked by how many corpus files hit\n"
        + "each one. Up to three example files are shown per group — this page is a ranked\n"
        + "index, NOT the full list; `docs/COMPATIBILITY-LEDGER.tsv` names every file with its\n"
        + "position. A refusal is a limitation stated out loud, and ADR 0001 prefers it to a\n"
        + "false success.\n\n"
        + "Of " + string total + " corpus files: **" + string t1 + "** proven, **" + string t2
        + "** admitted (parsed — NOT a\nparity claim), **" + string t3 + "** rejected. This page covers the rejected set.\n\n"
        + String.Join("\n",
            byReason |> List.map (fun (reason, count, examples) ->
                "## " + reason + "\n\n"
                + "Files: **" + string count + "**\n\n"
                + String.Join("\n", examples |> List.map (fun e -> "- `" + e + "`"))
                + (if count > 3 then "\n- …and " + string (count - 3) + " more (see the ledger)" else "")
                + "\n"))
        + ""

    let scorecardMd =
        "# Compatibility scorecard\n\n"
        + "Generated by `scripts/bin/generate-scorecard` (build it with `scripts/build-audits.sh`) from evidence on disk. Do not edit.\n\n"
        + "ADR 0001 fixes three tiers and forbids collapsing them into one number. "
        + "**No compatibility percentage is COMPUTED here**, and every count states its own\n"
        + "denominator. The one percentage below is QUOTED from ADR 0001's account of a prior\n"
        + "engine — it is the error being avoided, not a measurement of this one. The earlier\n"
        + "wording claimed no percentage APPEARED in the document, which the quotation itself\n"
        + "falsified: an absolute claim about the text, made in a document containing the\n"
        + "counterexample four paragraphs down.\n\n"
        + "## Corpus (third-party Jenkinsfiles, parse only)\n\n"
        + "| Tier | Meaning | Count |\n|---|---|---|\n"
        + "| 1 | proven compatible — a differential receipt names this file | " + string t1 + " of " + string total + " |\n"
        + "| 2 | ADR tier 2 (parses **and executes**) | **NOT ASSESSED** — corpus is never executed |\n"
        + "| — | admitted (parses only; **not an ADR tier**) | " + string t2 + " of " + string total + " |\n"
        + "| 3 | rejected — named error code and source position | " + string t3 + " of " + string total + " |\n\n"
        + "**The admitted row is not ADR tier 2.** The ADR requires parsing AND executing; this "
        + "scorer only parses, because corpus files are untrusted third-party CI code and are never "
        + "run here. Labelling them tier 2 would assert an execution result nobody measured, so ADR "
        + "tier 2 is published as NOT ASSESSED.\n\n"
        + "**Receipt seals are verified, but not by this generator.** A receipt is counted here by "
        + "its verdict line. That line is bound by the seal (FG-161), and the gate recomputes every "
        + "seal from the receipt's own content via `--verify-seals` on the differential CLI — where "
        + "the hash is computed, rather than reimplemented in a second language, which is how the "
        + "three existing copies of the timestamp rule came to disagree. So a doctored receipt fails "
        + "the gate; it does not fail this script, and the two are not independent checks.\n\n"
        + "**What the seal covers is a SUBSET of what a receipt prints.** EACH RECEIPT STATES ITS OWN unsealed "
        + "regions in full, under `## Comparison contract` — this page deliberately does not restate them. It "
        + "did, and the two lists drifted apart across four review rounds, each fix completing one copy and "
        + "leaving the other short. A doctored receipt fails verification only if the doctoring touched a "
        + "sealed field.\n\n"
        + "implemented. Each receipt's `## Comparison contract` carries the full list.\n\n"
        + "What verification does NOT cover: whether each case on disk still matches the digest its receipt "
        + "recorded (freshness, watched by an mtime warning), and the unsealed regions each receipt names.\n\n"
        + (if not (List.isEmpty byCode) then
             "### Tier-3 rejections by code\n\n| Code | Files |\n|---|---|\n"
             + String.Join("\n", byCode |> List.map (fun (c, n) -> "| `" + c + "` | " + string n + " |"))
             + "\n\n"
           else "")
        + "## Differential case suite (hand-written cases — a DIFFERENT population)\n\n"
        + "| Expected | Present | Proven |\n|---|---|---|\n| " + string caseExpected + " | " + string casePresent + " | "
        + string caseProven + " of " + string caseExpected + " |\n\n"
        + (if caseMissing > 0 then
             "**" + string caseMissing + " expected receipt(s) MISSING** — a case exists with no receipt. "
             + "The proven count is measured against what the cases expect, so a missing receipt "
             + "shows as a shortfall instead of quietly leaving the fraction.\n\n"
           else "")
        + "**These two sections do not share a denominator.** Corpus files PROVEN by a receipt: "
        + "**" + string t1 + "** of " + string total + ". "
        + (if t1 = 0 then "Every receipt proves a hand-written case, not a corpus file. "
           else "Those files appear as tier 1 in the corpus table above and are the only ones whose parity is proven. ")
        + "Reading the receipt count against the corpus "
        + "count would produce exactly the false ratio ADR 0001 was written to prevent — the "
        + "prior engine's 146 IRs against 5 proven files, which a single percentage would have "
        + "reported as 64%.\n"

    // WARNINGS BEFORE THE BRANCH. `build-and-test.sh` runs `--check` only, so
    // emitting these in the write branch meant the AUTOMATED path printed nothing
    // while reporting the suite as fully proven.
    for KeyValue(n, v) in receipts do
        if v = WrongCore then
            out ("WARN: receipt " + n + " was produced against a different jenkins-core — not counted as proven")
    for n in staleReceipts do
        out ("WARN: receipt " + n + " is OLDER than its case — the case was edited after the proof; re-run the suite")

    if check then
        let differs (rel: string) (want: string) =
            let p = Path.Combine(root, rel)
            not (File.Exists p) || slurp p <> want
        let stale =
            [ if differs "docs/COMPATIBILITY-LEDGER.tsv" ledgerTsv then yield "docs/COMPATIBILITY-LEDGER.tsv"
              if differs "docs/COMPATIBILITY-SCORECARD.md" scorecardMd then yield "docs/COMPATIBILITY-SCORECARD.md"
              if differs "docs/KNOWN-LIMITATIONS.md" limitationsMd then yield "docs/KNOWN-LIMITATIONS.md" ]
        if not (List.isEmpty stale) then
            out "FAIL: generated artifacts are stale — regenerate with scripts/build-audits.sh && scripts/bin/generate-scorecard"
            for f in stale do out ("   " + f)
            exitWith 1
        else
            out ("scorecard artifacts current: tier1=" + string t1 + " admitted=" + string t2 + " tier3=" + string t3
                 + " of " + string total + " corpus files; " + string caseProven + "/" + string caseExpected
                 + " expected case receipts proven"
                 + (if caseMissing > 0 then " — " + string caseMissing + " MISSING" else ""))
    else
        spit (Path.Combine(root, "docs/COMPATIBILITY-LEDGER.tsv")) ledgerTsv
        spit (Path.Combine(root, "docs/COMPATIBILITY-SCORECARD.md")) scorecardMd
        spit (Path.Combine(root, "docs/KNOWN-LIMITATIONS.md")) limitationsMd
        out "wrote docs/COMPATIBILITY-LEDGER.tsv and docs/COMPATIBILITY-SCORECARD.md"
        out ("corpus: tier1=" + string t1 + " admitted(not a tier)=" + string t2 + " tier3=" + string t3 + " of " + string total)
        out ("cases:  " + string caseProven + " proven of " + string caseExpected + " expected"
             + (if caseMissing > 0 then " — " + string caseMissing + " MISSING" else "")
             + " (separate denominator)")
    0
