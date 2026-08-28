#load "prelude.fsx"
/// FG-162 plus the FG-224 accounting closure. Compatibility tokens are derived from
/// the committed ledger, and the one live BOARD ACCOUNTING line is derived from the
/// canonical Wave ticket rows. The latter closes the exact gap that let prose advance
/// from 192 to 193 rows while the tables already contained 207.
///
/// Canonical ticket accounting semantics:
///   - rows begin `| FG-` between `## Wave 0` and `## Standing risks`;
///   - Waves 0, 1, 2, 3, 3.5, 3.6 and 4..9 each appear once and are nonempty;
///   - ids are unique, priorities are P0..P3, and statuses are the board's four-state
///     vocabulary (DONE, TODO, PARTIAL, BLOCKED);
///   - MOVED/SUPERSEDED/Retired rows exist at, and carry the status of, their target;
///   - open means every legal status except DONE;
///   - exactly one anchored `BOARD ACCOUNTING (derived)` line publishes the totals.
///
/// WHAT IT DOES NOT CHECK:
///   - arbitrary prose numbers: volatile totals belong only in the anchored summary;
///   - freshness of the compatibility ledger (`generate-scorecard --check` owns it);
///   - quoted historical tier tokens, which are deliberately exempt.
///
/// usage: scripts/bin/audit-board-numbers [board-file ledger-file]
///
/// Ported from `audit-board-numbers.bb` under FG-226.
open System
open System.IO
open System.Text.RegularExpressions
open Prelude

let legalPriorities = set [ "P0"; "P1"; "P2"; "P3" ]
let legalStatuses = set [ "DONE"; "TODO"; "PARTIAL"; "BLOCKED" ]
let expectedWaveLabels = [ "0"; "1"; "2"; "3"; "3.5"; "3.6"; "4"; "5"; "6"; "7"; "8"; "9" ]
let expectedWaveSet = Set.ofList expectedWaveLabels

let frequencies (xs: string seq) =
    xs |> Seq.fold (fun (m: Map<string, int>) k ->
        m.Add(k, (match m.TryFind k with Some v -> v + 1 | None -> 1))) Map.empty

let freqGet (m: Map<string, int>) k = match m.TryFind k with Some v -> v | None -> 0

/// `(nth parts idx "")` then strip bold markers then trim.
let cell (parts: string[]) (idx: int) =
    let raw = if idx < parts.Length then parts.[idx] else ""
    javaTrim (raw.Replace("**", ""))

/// Clojure `(pr-str s)` for a string — quoted, with backslash and quote escaped.
let prStr (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

let redirectRx = Regex(javaPattern "^(?:MOVED to|SUPERSEDED by|Retired by)\\s+(FG-\\d+[a-z]?)", RegexOptions.IgnoreCase)

let redirectTarget (detail: string) =
    let m = redirectRx.Match detail
    if m.Success then Some(m.Groups.[1].Value.ToUpperInvariant()) else None

type Row =
    { LineNumber: int
      Line: string
      Wave: string
      Structural: bool
      Id: string
      Priority: string
      Status: string
      Redirect: string option }

[<EntryPoint>]
let main argv =
    // Starting at the running binary's directory, the repository root is TWO
    // `..` segments up: `scripts/bin` -> `scripts` -> root. Counted from the
    // executable path itself, root is its third ancestor. The babashka original
    // needed one fewer `..` segment because its `*file*` was
    // `scripts/<tool>.bb`, one directory shallower. Spelled out rather than
    // inherited from the working directory, because a checker must name its target.
    //
    // The reference-sweep rewrote this comment's `.bb` path to the `.fsx` one
    // and thereby broke its own arithmetic — two ancestors up from
    // `scripts/fsx/<tool>.fsx` is `scripts`, not the root. Caught by the
    // pre-push verifier.
    let exeDir = Path.GetDirectoryName(Environment.ProcessPath)
    let root = Path.GetFullPath(Path.Combine(exeDir, "..", ".."))
    let ledgerFile =
        if argv.Length >= 2 then argv.[1] else Path.Combine(root, "docs/COMPATIBILITY-LEDGER.tsv")
    let boardFile =
        if argv.Length >= 1 then argv.[0] else Path.Combine(root, "docs/EXECUTION_BOARD.md")

    if not (File.Exists ledgerFile) then
        out "FAIL: docs/COMPATIBILITY-LEDGER.tsv missing — board numbers cannot be derived"
        exitWith 1
    if not (File.Exists boardFile) then
        out ("FAIL: board file not found: " + boardFile)
        exitWith 1

    // `(str/split % #"\t" -1)` keeps trailing empties, so a line with one field
    // still yields a second cell of "". A trailing-empty-dropping split would
    // turn an empty tier into a missing one, so this uses Regex.Split directly.
    let tabRx = Regex("\t")
    let tiers =
        splitLines (slurp ledgerFile)
        |> List.filter (fun l ->
            not (blank l || l.StartsWith("#", StringComparison.Ordinal) || l.StartsWith("file\t", StringComparison.Ordinal)))
        |> List.map (fun l ->
            let parts = tabRx.Split l
            if parts.Length >= 2 then parts.[1] else null)
    let countTier (v: string) = tiers |> List.filter (fun t -> t = v) |> List.length
    let compatibility =
        Map.ofList [ "tier1", countTier "1"; "tier3", countTier "3"; "admitted", countTier "admitted" ]

    let board = slurp boardFile
    let lines = splitLines board |> List.toArray

    let firstIndex (pred: string -> bool) =
        lines |> Array.tryFindIndex pred

    let waveStart = firstIndex (fun l -> l.StartsWith("## Wave ", StringComparison.Ordinal))
    let standingStart = firstIndex (fun l -> l = "## Standing risks")
    let validRegion =
        match waveStart, standingStart with
        | Some w, Some s -> w < s
        | _ -> false
    let ws = defaultArg waveStart 0
    let ss = defaultArg standingStart 0

    let waveHeadingRx = javaRx "^## Wave ([^ ]+)"
    let waveHeadings =
        if not validRegion then []
        else
            [ for idx in 0 .. lines.Length - 1 do
                if idx >= ws && idx < ss then
                    let m = waveHeadingRx.Match lines.[idx]
                    if m.Success then yield (idx, m.Groups.[1].Value) ]

    let waveFrequencies = frequencies (waveHeadings |> List.map snd)

    let rowRx = javaRx "^\\|\\s*FG-"
    let rowLines =
        if not validRegion then []
        else
            [ for idx in 0 .. lines.Length - 1 do
                if idx > ws && idx < ss && rowRx.IsMatch lines.[idx] then
                    let wave =
                        waveHeadings
                        |> List.filter (fun (hidx, _) -> hidx <= idx)
                        |> List.tryLast
                        |> Option.map snd
                        |> Option.defaultValue null
                    yield (idx + 1, lines.[idx], wave) ]

    let pipeRx = Regex("\\|")
    let idRx = javaRx "^FG-\\d+[a-z]?$"
    let parsedRows =
        rowLines
        |> List.map (fun (lineNumber, line, wave) ->
            let parts = pipeRx.Split line
            let id = cell parts 1
            { LineNumber = lineNumber
              Line = line
              Wave = wave
              Structural = parts.Length >= 7 && idRx.IsMatch id
              Id = id
              Priority = cell parts 2
              Status = cell parts 3
              Redirect = redirectTarget (cell parts 5) })

    let structuralRows = parsedRows |> List.filter (fun r -> r.Structural)
    let legalRows =
        structuralRows
        |> List.filter (fun r -> legalPriorities.Contains r.Priority && legalStatuses.Contains r.Status)
    let duplicates =
        structuralRows
        |> List.map (fun r -> r.Id)
        |> frequencies
        |> Map.toList
        |> List.filter (fun (_, n) -> n > 1)
    let rowsById =
        structuralRows |> List.fold (fun (m: Map<string, Row>) r -> m.Add(r.Id, r)) Map.empty
    let rowsByWave = frequencies (legalRows |> List.map (fun r -> r.Wave))
    let statusFrequencies = frequencies (legalRows |> List.map (fun r -> r.Status))
    let openRows = legalRows |> List.filter (fun r -> r.Status <> "DONE")
    let openPriorities = frequencies (openRows |> List.map (fun r -> r.Priority))
    let accounting =
        Map.ofList
            [ "rows", List.length legalRows
              "DONE", freqGet statusFrequencies "DONE"
              "open", List.length openRows
              "P0", freqGet openPriorities "P0"
              "P1", freqGet openPriorities "P1"
              "P2", freqGet openPriorities "P2"
              "P3", freqGet openPriorities "P3" ]

    let summaryRx =
        javaRx "(?m)^\\*\\*BOARD ACCOUNTING \\(derived\\): rows=(\\d+); DONE=(\\d+); open=(\\d+); open P0–P3=(\\d+) / (\\d+) / (\\d+) / (\\d+)\\.\\*\\*$"
    let summaryMatches = summaryRx.Matches board |> Seq.toList
    let summaryValues =
        if List.length summaryMatches = 1 then
            let g = (List.head summaryMatches).Groups
            Some(
                Map.ofList
                    [ "rows", int g.[1].Value
                      "DONE", int g.[2].Value
                      "open", int g.[3].Value
                      "P0", int g.[4].Value
                      "P1", int g.[5].Value
                      "P2", int g.[6].Value
                      "P3", int g.[7].Value ])
        else None

    let findings =
        [ if not validRegion then
            yield "canonical Wave region is missing or ends before it starts"

          for label in expectedWaveLabels do
            if freqGet waveFrequencies label = 0 then
                yield "Wave " + label + " — expected heading is missing"

          for KeyValue(label, n) in waveFrequencies do
            if n > 1 then yield "Wave " + label + " — heading appears " + string n + " times"

          for label in waveFrequencies |> Map.toList |> List.map fst |> List.filter (fun l -> not (expectedWaveSet.Contains l)) do
            yield "Wave " + label + " — unexpected heading"

          for label in expectedWaveLabels do
            if freqGet waveFrequencies label = 1 && freqGet rowsByWave label = 0 then
                yield "Wave " + label + " — contains no legal canonical ticket row"

          for r in parsedRows do
            if not r.Structural then
                yield "line " + string r.LineNumber + " — malformed canonical ticket row"

          for r in structuralRows do
            if not (legalPriorities.Contains r.Priority) then
                yield "line " + string r.LineNumber + " — illegal priority " + prStr r.Priority

          for r in structuralRows do
            if not (legalStatuses.Contains r.Status) then
                yield "line " + string r.LineNumber + " — illegal status " + prStr r.Status

          for (id, n) in duplicates do
            yield id + " — duplicate canonical id appears " + string n + " times"

          for r in structuralRows do
            match r.Redirect with
            | Some t when not (rowsById.ContainsKey t) ->
                yield r.Id + " — redirect target " + t + " does not exist"
            | _ -> ()

          for r in structuralRows do
            match r.Redirect with
            | Some t ->
                match rowsById.TryFind t with
                | Some target when r.Status <> target.Status ->
                    yield r.Id + " — redirect status " + r.Status + " disagrees with " + t + " status " + target.Status
                | _ -> ()
            | None -> ()

          match List.length summaryMatches with
          | 0 -> yield "missing anchored BOARD ACCOUNTING (derived) summary"
          | 1 -> ()
          | n -> yield "expected one anchored BOARD ACCOUNTING (derived) summary; found " + string n

          match summaryValues with
          | Some sv ->
              for kind in [ "rows"; "DONE"; "open"; "P0"; "P1"; "P2"; "P3" ] do
                  let stated = sv.[kind]
                  let derived = accounting.[kind]
                  if stated <> derived then
                      yield "accounting " + kind + "=" + string stated + " — canonical Wave rows derive " + kind + "=" + string derived
          | None -> ()

          // Live tier2= claims are refused: ADR tier 2 is published as NOT ASSESSED.
          for m in (javaRx "([\"]?)tier2=\\*{0,2}(\\d+)").Matches board do
            if m.Groups.[1].Value <> "\"" then
                yield "tier2=" + m.Groups.[2].Value + " — ADR tier 2 is NOT ASSESSED; no live claim may use this token"

          // Live compatibility tokens must match the generated ledger.
          for m in (javaRx "([\"]?)(tier1|tier3|admitted)=\\*{0,2}(\\d+)\\*{0,2}").Matches board do
            let kind = m.Groups.[2].Value
            let n = m.Groups.[3].Value
            let want = compatibility.[kind]
            if m.Groups.[1].Value <> "\"" && int n <> want then
                yield kind + "=" + n + " — the ledger derives " + kind + "=" + string want ]

    if not (List.isEmpty findings) then
        out ("BOARD-NUMBER AUDIT FAILED (" + string (List.length findings) + "):")
        for f in findings do out ("   " + f)
        out "Fix the canonical rows or the one derived summary; regenerate the ledger only if its compatibility counts are wrong."
        exitWith 1
    else
        out ("board accounting consistent: rows=" + string accounting.["rows"]
             + " DONE=" + string accounting.["DONE"]
             + " open=" + string accounting.["open"]
             + " open-P0..P3=" + String.Join("/", [ "P0"; "P1"; "P2"; "P3" ] |> List.map (fun k -> string accounting.[k]))
             + "; compatibility ledger: tier1=" + string compatibility.["tier1"]
             + " tier3=" + string compatibility.["tier3"]
             + " admitted=" + string compatibility.["admitted"])
    0
