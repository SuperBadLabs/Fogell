namespace Fogell.Differential

open System
open System.IO
open System.Security.Cryptography

/// Why a comparison failed, named so a report is machine-readable.
type Divergence =
    | ResultDiffers of jenkins: string * fogell: string
    | DiagnosticSilence of engine: string
    /// One engine printed `timestamps()` prefixes and the other did not. The
    /// prefix TEXT is excluded from line comparison — two clocks never agree —
    /// so without this the exclusion would let an engine that IGNORES the option
    /// compare equal to one that honours it, which is the exact shape of hole
    /// FG-102's rule exists to prevent.
    | TimestampMismatch of jenkins: string * fogell: string
    | OutputDiffers of firstMismatchIndex: int * jenkins: string option * fogell: string option
    | WorkspaceDiffers of jenkins: string * fogell: string
    | JenkinsFailed of string
    | FogellFailed of string

    member this.Describe =
        match this with
        | ResultDiffers(j, f) -> $"terminal result: jenkins={j} fogell={f}"
        | DiagnosticSilence engine -> $"{engine} failed without reporting a reason"
        | TimestampMismatch(j, f) ->
            $"timestamps() coverage differs: jenkins={j} fogell={f} — the option is honoured differently by the two engines"
        | OutputDiffers(i, j, f) ->
            let show = Option.defaultValue "<absent>"
            $"output line {i}: jenkins={show j} fogell={show f}"
        | WorkspaceDiffers(j, f) -> $"workspace hash: jenkins={j.Substring(0, 12)} fogell={f.Substring(0, 12)}"
        | JenkinsFailed e -> $"jenkins side failed: {e}"
        | FogellFailed e -> $"fogell side failed: {e}"

/// The verdict for one file. ADR 0001's tiers, decided by evidence.
type Verdict =
    /// Same result and same output. Whether the WORKSPACE was also compared is
    /// carried separately, because a receipt that says "same workspace" when no
    /// workspace was collected is precisely the vacuous claim this harness
    /// exists to prevent.
    | Proven
    /// Ran on both, but they disagree. NOT a Fogell bug by assumption — the
    /// divergence is named so it can be adjudicated.
    | Diverged of Divergence list
    /// One side could not run it at all.
    | NotComparable of Divergence

type Receipt =
    { File: string
      Verdict: Verdict
      JenkinsCore: string
      Jenkins: Trace option
      Fogell: Trace option
      ComparisonContract: string list
      /// Output pairs that compared CANONICALLY (inherited-env fold) rather than
      /// byte-equal, listed per case so the relaxation is visible in the receipt
      /// that used it, never only in the rule's statement.
      FoldedOutputPairs: string list
      /// FG-119. Divergences seen on EARLIER attempts of this case that did not
      /// reproduce. Empty for a case proven on its first run.
      ///
      /// This lives in the RECEIPT and not only in the run's console output,
      /// because the receipt is the artifact that gets committed and read months
      /// later. Keeping recovery in the console alone left a re-run receipt
      /// byte-identical to a first-attempt pass — which is precisely what the
      /// retry logic's own comment promised would never happen. Caught by the
      /// pre-push verifier's model review.
      RecoveredFrom: string list
      /// Hash over the receipt's COMPARED CONTENT — the two engines' results,
      /// output and workspace — so the evidence a verdict rests on cannot be
      /// edited after the fact without detection.
      ///
      /// It does NOT cover [RecoveredFrom], which is run provenance rather than
      /// compared content and is deliberately excluded so that a case proven on
      /// attempt 1 and the same case proven on attempt 2 seal identically. The
      /// consequence, stated because the previous wording ("a receipt cannot be
      /// edited") implied otherwise: the RECOVERED block CAN be removed from a
      /// committed receipt without breaking the seal. Restoring that guarantee
      /// needs a second provenance hash — FG-128, not smuggled in behind a
      /// comment. Caught by the pre-push verifier's model review.
      Seal: string }

module Compare =

    let private sha256Text (text: string) =
        use h = SHA256.Create()

        Text.Encoding.UTF8.GetBytes text
        |> h.ComputeHash
        |> Convert.ToHexString
        |> fun s -> s.ToLowerInvariant()

    /// FG-036. Ordering relaxation for parallel runs, stated rather than hidden.
    ///
    /// Two concurrent branches produce their lines in whatever order the OS
    /// scheduler chose. Comparing that as a SEQUENCE would make the receipt a
    /// race: it would pass or fail on scheduling, not on semantics. So for a
    /// pipeline containing a `parallel` block, output is compared as a sorted
    /// MULTISET — every line must still be present, exactly as many times — but
    /// the interleaving is not compared.
    ///
    /// MEASURED: declarative Jenkins does NOT prefix branch output with
    /// `[branchName]`. That prefix belongs to the SCRIPTED `parallel` map form.
    /// An earlier version of Fogell emitted it and produced a divergence on every
    /// parallel case; the fix was to stop inventing attribution Jenkins does not
    /// provide. It also means the relaxation genuinely loses information — with
    /// no prefix, a line cannot be attributed to a branch at all — which is why
    /// the parallel receipts lean on the WORKSPACE hash for their real claim.
    /// Receipt: `parallel-always-failfast` — it is the case whose branches actually WRITE
    /// TO STDOUT (`from-quick`, `from-slow`, both unprefixed). `parallel-siblings-finish`
    /// was cited first and cannot support this claim: its branches write FILES, so it emits
    /// no ordinary branch output at all.
    /// FG-102 round 48. dash's xtrace prints only the FIRST physical line of a
    /// multiline word with the `+ ` prefix; continuation lines are bare, and dash
    /// neither re-quotes nor marks the record's end (measured — /bin/sh is dash
    /// on both engines), so one-sided normalisation cannot attribute them.
    /// Resolution happens HERE, where both lines are visible: a mismatching pair
    /// that becomes equal under the SAME inherited-env replacement list applied
    /// to BOTH sides is the same expansion. Literals appear identically on both
    /// sides and cancel, so the rule collapses only genuine inherited-value
    /// differences — it can turn a divergence into an equality, never the
    /// reverse, and stored receipt lines keep each engine's literal text. The
    /// pair it cannot adjudicate — each side printing the OTHER's inherited
    /// value as a literal — requires intent and joins the stated mimicry
    /// residual.
    let private compareOutput
        (concurrent: bool)
        (envReplacements: (string * string) list)
        (jenkins: string list)
        (fogell: string list)
        =
        let ordered =
            envReplacements
            |> List.filter (fun ((v: string), _) -> v <> "" && v.Length >= 4)
            |> List.sortByDescending (fun ((v: string), _) -> v.Length)

        let canon (l: string) =
            ordered |> List.fold (fun (acc: string) (v, token) -> acc.Replace(v, token)) l

        // Every fold is REPORTED, not just applied: the receipt lists each pair
        // that compared canonically rather than byte-equal, so the relaxation is
        // visible per case instead of buried in the rule (round 48 P1 — the
        // trace-record evidence a gate would need does not exist on this channel,
        // so the answer to "this could hide a difference" is to print it).
        let rec walk i (folds: string list) j f =
            match j, f with
            | [], [] -> None, List.rev folds
            | jh :: jt, fh :: ft ->
                if jh = fh then
                    walk (i + 1) folds jt ft
                elif canon jh = canon fh then
                    walk (i + 1) ($"line {i} compared canonically: {canon jh}" :: folds) jt ft
                else
                    Some(OutputDiffers(i, Some jh, Some fh)), List.rev folds
            | jh :: _, [] -> Some(OutputDiffers(i, Some jh, None)), List.rev folds
            | [], fh :: _ -> Some(OutputDiffers(i, None, Some fh)), List.rev folds

        if concurrent then
            // Canonicalise BEFORE sorting so resolved-equal lines sort together;
            // a multiset mismatch therefore reports canonical text, and the
            // receipt's stored per-engine output remains literal. Indices are
            // meaningless after sorting, so folding is reported as per-side counts.
            let touched side = side |> List.filter (fun l -> canon l <> l) |> List.length
            let jt, ft = touched jenkins, touched fogell

            let d, _ =
                walk 0 [] (jenkins |> List.map canon |> List.sort) (fogell |> List.map canon |> List.sort)

            d,
            (if jt + ft > 0 then
                 [ $"multiset mode: inherited-env canonicalisation touched {jt} jenkins / {ft} fogell lines" ]
             else
                 [])
        else
            walk 0 [] jenkins fogell

    /// Compare two traces. Workspace hashes are only compared when BOTH sides
    /// collected one — claiming a match against "not-collected" would be exactly
    /// the sort of vacuous pass this harness exists to prevent.
    /// True only when BOTH sides produced a real workspace hash.
    let workspaceWasCompared (jenkins: Trace) (fogell: Trace) =
        jenkins.WorkspaceHash <> "not-collected" && fogell.WorkspaceHash <> "not-collected"

    let traces (envReplacements: (string * string) list) (jenkins: Trace) (fogell: Trace) : Verdict * string list =
        let outputDivergence, folds =
            compareOutput (jenkins.Concurrent || fogell.Concurrent) envReplacements jenkins.Output fogell.Output

        let divergences =
            [ if jenkins.Result <> fogell.Result then
                  ResultDiffers(jenkins.Result, fogell.Result)

              match outputDivergence with
              | Some d -> d
              | None -> ()

              // A FAILED or ABORTED build must be explained by both engines.
              // `unstable` is excluded deliberately: Jenkins marks a build
              // unstable from a test report and prints no ERROR line — the report
              // is the explanation. Requiring one there fired symmetrically on
              // both engines, which is the signature of a wrong rule rather than
              // a real divergence.
              if jenkins.Result = "failure" || jenkins.Result = "aborted" then
                  if not fogell.ReportedFailureReason then DiagnosticSilence "fogell"
                  if not jenkins.ReportedFailureReason then DiagnosticSilence "jenkins"

              // COMPARED, not excluded. `normaliseLine` strips the prefix so the
              // instants never have to agree; this is what stops that strip from
              // hiding a missing implementation.
              // CLASSIFIED, not counted: the engines never print the same
              // number of lines, so only none/partial/all can be compared. A
              // partial stamper no longer passes against a full one, which an
              // `exists` boolean allowed.
              // CLASSIFICATION ONLY — and the first version compared
              // `partial(1/2)` strings, which put the counts back into the
              // comparison the comment above says are not comparable. Two
              // partial stampers with different line counts would have diverged
              // on the counts rather than on the behaviour. Counts survive in
              // the MESSAGE, where they help a reader, and nowhere else.
              let classify = Trace.timestampCoverage

              let describe (stamped, total) =
                  match classify (stamped, total) with
                  | "partial" -> $"partial ({stamped}/{total})"
                  | c -> c

              if classify jenkins.Timestamps <> classify fogell.Timestamps then
                  TimestampMismatch(describe jenkins.Timestamps, describe fogell.Timestamps)

              if
                  jenkins.WorkspaceHash <> "not-collected"
                  && fogell.WorkspaceHash <> "not-collected"
                  && jenkins.WorkspaceHash <> fogell.WorkspaceHash
              then
                  WorkspaceDiffers(jenkins.WorkspaceHash, fogell.WorkspaceHash) ]

        (if List.isEmpty divergences then Proven else Diverged divergences), folds

    let receipt
        (file: string)
        (core: string)
        (envReplacements: (string * string) list)
        (jenkins: Result<Trace, string>)
        (fogell: Result<Trace, string>)
        : Receipt =
        let (verdict, folds), j, f =
            match jenkins, fogell with
            | Result.Error e, _ -> (NotComparable(JenkinsFailed e), []), None, None
            | _, Result.Error e -> (NotComparable(FogellFailed e), []), None, None
            | Result.Ok jt, Result.Ok ft -> traces envReplacements jt ft, Some jt, Some ft

        let comparable =
            let render (t: Trace option) =
                match t with
                | Some x ->
                    let joined = String.concat "\n" x.Output
                    // The timestamp coverage is IN THE SEAL. It is compared, so a receipt
                    // that did not bind it would let the fact be edited out
                    // without changing the hash — a sealed document asserting
                    // something its seal does not cover. The prefix TEXT stays
                    // out (two clocks), the FACT is bound.
                    // the SAME classifier the comparison uses. Three copies of
                    // this rule existed — compare, seal, render — and two of them
                    // disagreed about `stamped > total`, so a proof could compare
                    // one fact and seal another.
                    $"{x.Result}|{x.WorkspaceHash}|timestamps={Trace.timestampCoverage x.Timestamps}|{joined}"
                | None -> "<none>"

            // Folds join the sealed content: a fold section edited after the
            // fact must be as detectable as an edited output line.
            let joinedFolds = String.concat "\n" folds
            $"{file}\n{core}\n{render j}\n{render f}\n{joinedFolds}"

        { File = file
          Verdict = verdict
          JenkinsCore = core
          Jenkins = j
          Fogell = f
          ComparisonContract =
            Trace.comparisonContract
            @ (if [ j; f ] |> List.choose id |> List.exists (fun t -> t.Concurrent) then
                   [ "PARALLEL: output compared as a sorted multiset, not a sequence — concurrent"
                     "  branches interleave nondeterministically, so a sequence comparison would"
                     "  pass or fail on OS scheduling. Line content and multiplicity ARE compared;"
                     "  order is NOT, and declarative Jenkins emits no branch prefix to attribute"
                     "  a line by. The load-bearing claim for these cases is the workspace hash." ]
               else
                   [])
          FoldedOutputPairs = folds
          // Deliberately NOT part of `comparable`, so the seal still hashes what
          // the two engines produced. A case proven on attempt 1 and the same case
          // proven on attempt 2 must seal identically — recovery is provenance
          // about the RUN, not about the compared content.
          RecoveredFrom = []
          Seal = sha256Text comparable }

    /// Render a receipt as text. Deliberately plain so it can be committed,
    /// diffed and hashed alongside the code.
    let render (r: Receipt) : string =
        let sb = Text.StringBuilder()
        let line (s: string) = sb.AppendLine s |> ignore

        line $"# Differential receipt — {r.File}"
        line ""
        line $"jenkins-core: {r.JenkinsCore}"
        line $"seal:         {r.Seal}"
        line ""

        let workspaceCompared =
            match r.Jenkins, r.Fogell with
            | Some j, Some f -> workspaceWasCompared j f
            | _ -> false

        if not (List.isEmpty r.RecoveredFrom) then
            line "RECOVERED: this case DIVERGED on an earlier attempt and did not reproduce."
            line "  The verdict below is from a re-run. What the earlier attempt showed:"

            for d in r.RecoveredFrom do
                line $"    {d}"

            line "  NOTE: this block is run provenance and is NOT covered by the seal"
            line "  above, which hashes the compared content so a verdict's evidence cannot"
            line "  be altered undetected. See FG-128."
            line "  CAUSE UNCLASSIFIED. The FG-119 retry re-runs EVERY divergence, not"
            line "  only the known `sh -x` pipeline interleaving, and nothing here"
            line "  establishes which this was — a genuine intermittent engine mismatch"
            line "  that happened to pass on re-run looks exactly the same. Naming the"
            line "  trace race would let a real defect read as classified noise. A case"
            line "  that recovers REPEATEDLY across runs is a defect report; treat it so."
            line ""

        match r.Verdict with
        | Proven when workspaceCompared ->
            line "VERDICT: PROVEN (tier 1) — same result, same output, same workspace hash"
        | Proven ->
            line "VERDICT: PROVEN-PARTIAL — same result, same output."
            line "  WORKSPACE NOT COMPARED: no workspace was collected from at least one side,"
            line "  so this is NOT a tier-1 claim under ADR 0001. See FG-002b."
        | Diverged ds ->
            line $"VERDICT: DIVERGED ({ds.Length})"

            for d in ds do
                line $"  - {d.Describe}"
        | NotComparable d ->
            line "VERDICT: NOT COMPARABLE"
            line $"  - {d.Describe}"

        // The timestamp fact, rendered WHEN THERE IS ANY — and the sentence here
        // used to say "a receipt that compares something must show it", which
        // this does not do. The fact is compared and sealed for every case; it
        // is PRINTED only when a side stamped at least one line, so a declared
        // `timestamps()` case where both classify `none` seals the fact and
        // shows nothing.
        //
        // Kept conditional rather than widened: printing it on all 102 receipts,
        // almost all of which never mention the option, is noise that would
        // train readers past the line. The honest fix is the wording, since the
        // case it hides — declared but stamped nowhere — is a divergence on
        // every OTHER axis anyway (the engine that honoured it stamped, so its
        // count is non-zero and the line appears).
        match r.Jenkins, r.Fogell with
        | Some j, Some f when fst j.Timestamps > 0 || fst f.Timestamps > 0 ->
            line ""

            let show ((stamped, total) as ts) =
                match Trace.timestampCoverage ts with
                | "partial" -> $"PARTIAL ({stamped}/{total})"
                | "all" -> $"all ({total})"
                | c -> c

            line
                $"timestamps(): jenkins={show j.Timestamps} fogell={show f.Timestamps} (prefix text excluded, coverage compared and sealed)"
        | _ -> ()

        // Every fold this case USED, printed in the case that used it. A reader
        // of this receipt alone sees exactly which output pairs were accepted
        // canonically instead of byte-equal — the relaxation is never invisible.
        if not (List.isEmpty r.FoldedOutputPairs) then
            line ""
            line $"## Output pairs compared canonically — inherited env ({r.FoldedOutputPairs.Length})"

            for n in r.FoldedOutputPairs do
                line $"  {n}"

        line ""
        line "## Comparison contract"

        for c in r.ComparisonContract do
            line $"  {c}"

        let renderSide name (t: Trace option) =
            line ""
            line $"## {name}"

            match t with
            | None -> line "  (did not run)"
            | Some x ->
                line $"  result:         {x.Result}"
                line $"  workspace-hash: {x.WorkspaceHash}"
                line $"  output ({x.Output.Length} lines):"

                for l in x.Output do
                    line $"    | {l}"

                if not (List.isEmpty x.WorkspaceFiles) then
                    line "  workspace:"

                    for path, hash in x.WorkspaceFiles do
                        line $"    {hash.Substring(0, 12)}  {path}"

                // FG-103: the engine reporting on its own checks. Printed, never
                // compared — see the comparison contract.
                if not (List.isEmpty x.EngineNotes) then
                    line "  engine notes (not compared):"

                    for note in x.EngineNotes do
                        line $"    ! {note}"

        renderSide "Jenkins" r.Jenkins
        renderSide "Fogell" r.Fogell
        sb.ToString()

    let seal (directory: string) (r: Receipt) : string =
        Directory.CreateDirectory directory |> ignore
        let safe = r.File.Replace("/", "_").Replace(".Jenkinsfile", "")
        let path = Path.Combine(directory, $"{safe}.receipt.txt")
        File.WriteAllText(path, render r)
        path
