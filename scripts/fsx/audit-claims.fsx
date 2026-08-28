#load "prelude.fsx"
/// FG-104. Every code comment that asserts MEASURED Jenkins behaviour must name the
/// receipt that proves it.
///
/// Three times a comment of mine became the specification the code silently disagreed
/// with. A claim nobody can check is not evidence, it is a rumour with a citation style.
/// This makes the rule mechanical: a MEASURED claim naming no existing receipt is a defect.
///
/// WHAT IT CANNOT DO, stated so nobody mistakes a pass for proof: it checks that a receipt
/// is NAMED, never that the named receipt EXERCISES the claim. The check is a floor, not a
/// ceiling; a human still has to open the receipt.
///
///   usage: scripts/bin/audit-claims [--strict]
///          --strict exits non-zero when any claim is unbacked
///
/// Ported from `audit-claims.bb` under FG-226. ONE TRANSLATION HAZARD WORTH NAMING:
/// the original quoted citation names with `\Q...\E`, which java.util.regex supports
/// and .NET DOES NOT — .NET would read those four characters literally and no
/// citation would ever match, silently turning this audit into a no-op that passes
/// everything. `Regex.Escape` is the equivalent and is used at that site.
open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open Prelude

type Mode =
    | Code
    | Block
    | Str
    | Verbatim
    | Triple
    | Interp
    | InterpVerbatim
    | InterpTriple

type LineScan = { Spans: string list; Code: bool; Continues: bool }

let leadingSlashRx = javaRx "^/+\\s?"

/// Returns spans, end-of-line mode, block depth, dollar count, hole stack, and
/// whether the line carried code before its comment.
///
/// State is carried ACROSS lines, not reset per line. It has to be: both F# block
/// comments and F# string literals span lines, and this repo has
/// `"SELECT count(*) FILTER (...` opening a multi-line string in Store.fs. Scanning
/// each line fresh would read that continuation as code, see `(*`, and open a block
/// comment that swallows the rest of the file — turning every later line into
/// "comment text" where any receipt name could satisfy any claim. That is fail-OPEN.
let scanLine (l: string) (mode0: Mode) (depth0: int) (dollars0: int) (holes0: (Mode * int) list) =
    let n = l.Length
    let at (i: int) = if i >= 0 && i < n then l.[i] else '\000'
    let runOf (ch: char) (i: int) =
        let mutable j = i
        while at j = ch do j <- j + 1
        j - i

    let spans = ResizeArray<string>()
    let cur = ResizeArray<string>()
    let mutable mode = mode0
    let mutable depth = depth0
    let mutable dollars = dollars0
    let mutable holes = holes0
    let mutable gap = false
    let mutable code = false
    let mutable k = 0
    let mutable finished = false

    let flush () =
        if cur.Count > 0 then spans.Add(String.Join(" ", cur))
        cur.Clear()
    // entering a comment: close the current span first if code intervened
    let enter () = if gap && cur.Count > 0 then flush ()

    while not finished && k < n do
        match mode with
        | Code ->
            // interpolated forms first — `$` must not read as plain code + `"`
            if at k = '$' then
                let d = runOf '$' k
                if at (k + d) = '"' && at (k + d + 1) = '"' && at (k + d + 2) = '"' then
                    mode <- InterpTriple; dollars <- d; k <- k + d + 3; gap <- true; code <- true
                elif d = 1 && at (k + 1) = '@' && at (k + 2) = '"' then
                    mode <- InterpVerbatim; dollars <- 1; k <- k + 3; gap <- true; code <- true
                elif d = 1 && at (k + 1) = '"' then
                    mode <- Interp; dollars <- 1; k <- k + 2; gap <- true; code <- true
                else
                    k <- k + d; gap <- true; code <- true
            elif at k = '@' && at (k + 1) = '$' && at (k + 2) = '"' then
                mode <- InterpVerbatim; dollars <- 1; k <- k + 3; gap <- true; code <- true
            elif at k = '"' && at (k + 1) = '"' && at (k + 2) = '"' then
                mode <- Triple; k <- k + 3; gap <- true; code <- true
            elif at k = '@' && at (k + 1) = '"' then
                mode <- Verbatim; k <- k + 2; gap <- true; code <- true
            elif at k = '"' then
                mode <- Str; k <- k + 1; gap <- true; code <- true
            // '\n' — escaped char literal
            elif at k = '\'' && at (k + 1) = '\\' && at (k + 3) = '\'' then
                k <- k + 4; gap <- true; code <- true
            // 'x' — plain char literal, but NOT 'T (a generic type variable)
            elif at k = '\'' && (k + 1 < n) && at (k + 1) <> '\\' && at (k + 2) = '\'' then
                k <- k + 3; gap <- true; code <- true
            elif at k = '(' && at (k + 1) = '*' then
                enter (); mode <- Block; depth <- 1; k <- k + 2; gap <- false
            // `//` runs to end of line; `///` must consume all three slashes.
            elif at k = '/' && at (k + 1) = '/' then
                enter ()
                cur.Add(leadingSlashRx.Replace(l.Substring k, ""))
                spans.Add(String.Join(" ", cur))
                cur.Clear()
                finished <- true
            // hole bookkeeping: a nested `{` re-enters Code so its `}` cannot close
            // the hole early; a run of `}` matching the hole's brace count pops back.
            elif at k = '{' && not (List.isEmpty holes) then
                holes <- (Code, 1) :: holes; k <- k + 1; gap <- true; code <- true
            elif at k = '}' && not (List.isEmpty holes) then
                let (m, d) = List.head holes
                if runOf '}' k >= d then
                    // d is ALSO the enclosing string's dollar count — restore it.
                    mode <- m; dollars <- d; holes <- List.tail holes; k <- k + d; gap <- true; code <- true
                else
                    k <- k + 1; gap <- true; code <- true
            else
                let c = not (javaIsWhitespace (at k))
                gap <- gap || c
                code <- code || c
                k <- k + 1

        | Block ->
            if at k = '(' && at (k + 1) = '*' then
                depth <- depth + 1; k <- k + 2
            elif at k = '*' && at (k + 1) = ')' then
                if depth <= 1 then (mode <- Code; depth <- 0) else depth <- depth - 1
                k <- k + 2
            else
                // collect the block's text one char at a time
                if cur.Count = 0 then cur.Add(string (at k))
                else cur.[cur.Count - 1] <- cur.[cur.Count - 1] + string (at k)
                k <- k + 1

        | Str ->
            if at k = '\\' then k <- k + 2
            elif at k = '"' then (mode <- Code; k <- k + 1)
            else k <- k + 1

        // @"..." has no backslash escapes; "" is one literal quote
        | Verbatim ->
            if at k = '"' && at (k + 1) = '"' then k <- k + 2
            elif at k = '"' then (mode <- Code; k <- k + 1)
            else k <- k + 1

        | Triple ->
            if at k = '"' && at (k + 1) = '"' && at (k + 2) = '"' then (mode <- Code; k <- k + 3)
            else k <- k + 1

        | Interp ->
            if at k = '\\' then k <- k + 2
            elif at k = '{' && at (k + 1) = '{' then k <- k + 2
            elif at k = '}' && at (k + 1) = '}' then k <- k + 2
            elif at k = '{' then (holes <- (Interp, 1) :: holes; mode <- Code; k <- k + 1)
            elif at k = '"' then (mode <- Code; k <- k + 1)
            else k <- k + 1

        | InterpVerbatim ->
            if at k = '"' && at (k + 1) = '"' then k <- k + 2
            elif at k = '{' && at (k + 1) = '{' then k <- k + 2
            elif at k = '}' && at (k + 1) = '}' then k <- k + 2
            elif at k = '{' then (holes <- (InterpVerbatim, 1) :: holes; mode <- Code; k <- k + 1)
            elif at k = '"' then (mode <- Code; k <- k + 1)
            else k <- k + 1

        | InterpTriple ->
            if at k = '"' && at (k + 1) = '"' && at (k + 2) = '"' then (mode <- Code; k <- k + 3)
            // the {{ }} ESCAPE exists only at one dollar; at n dollars a run of n
            // braces OPENS the hole and shorter runs are literal text
            elif dollars = 1 && at k = '{' && at (k + 1) = '{' then k <- k + 2
            elif dollars = 1 && at k = '}' && at (k + 1) = '}' then k <- k + 2
            elif at k = '{' && runOf '{' k >= dollars then
                holes <- (InterpTriple, dollars) :: holes; mode <- Code; k <- k + dollars
            else k <- k + 1

    if not finished then flush ()
    (List.ofSeq spans, mode, depth, dollars, holes, code)

/// One fold per FILE, carrying the scanner's mode AND block-comment depth.
let scanFile (lines: string list) =
    let acc = ResizeArray<LineScan>()
    let mutable mode = Code
    let mutable depth = 0
    let mutable dollars = 1
    let mutable holes: (Mode * int) list = []
    for line in lines do
        let started = mode
        let (spans, m, d, dol, h, code) = scanLine line mode depth dollars holes
        mode <- m; depth <- d; dollars <- dol; holes <- h
        // Continues — the line STARTED inside a block comment, so even a blank line
        // there is comment interior. Without it, a blank line inside one multiline
        // (* ... *) produced no spans and broke the claim's block in half.
        acc.Add { Spans = spans; Code = code; Continues = (started = Block) }
    acc.ToArray()

/// A block may only grow across FULL-LINE comments. Accepting trailing comments as
/// claims does not make them block members.
let fullComment (s: LineScan) = (not (List.isEmpty s.Spans) || s.Continues) && not s.Code

[<EntryPoint>]
let main argv =
    let strict = argv |> Array.contains "--strict"
    // See audit-board-numbers for why this is three parents rather than two.
    let exeDir = Path.GetDirectoryName(Environment.ProcessPath)
    let root = Path.GetFullPath(Path.Combine(exeDir, "..", ".."))

    let receipts =
        glob (Path.Combine(root, "differential/receipts")) "*.receipt.txt"
        |> List.map (fun f -> Path.GetFileName(f).Replace(".receipt.txt", ""))
        |> Set.ofList

    // APPROVAL-LANE SCENARIOS COUNT AS EVIDENCE, and a receipt cannot replace them.
    // A receipt CANNOT observe whether an approval PROMPT WAS PUBLISHED. The citation
    // is VERIFIED, not free text: the scenario letter must exist in the lane script.
    let laneScenarios =
        let f = Path.Combine(root, "scripts/run-approval-lane.sh")
        if not (File.Exists f) then Set.empty
        else
            (javaRx "(?m)^echo \"=== ([A-Z][0-9]*):").Matches(slurp f)
            |> Seq.map (fun m -> "approval-lane scenario " + m.Groups.[1].Value)
            |> Set.ofSeq

    // PROOF-SCRIPT CASES ARE EVIDENCE TOO. ANY `expect_*` helper, not an enumerated
    // three: a prefix match cannot go stale when the next helper is written.
    let proofCases =
        let f = Path.Combine(root, "scripts/prove-section-refusals.sh")
        if not (File.Exists f) then Set.empty
        else
            (javaRx "(?m)^expect_[a-z_]+\\s+([a-z0-9-]+)").Matches(slurp f)
            |> Seq.map (fun m -> m.Groups.[1].Value)
            |> Set.ofSeq

    let citable = Set.union (Set.union receipts laneScenarios) proofCases

    // ALL F# sources, not just src/ — tools and tests carry MEASURED claims too, and a
    // check whose scope is narrower than its description is the defect this catches.
    let sources =
        [ "src"; "tools"; "tests" ]
        |> List.collect (fun d ->
            let dir = Path.Combine(root, d)
            if Directory.Exists dir then
                Directory.GetFiles(dir, "*.fs", SearchOption.AllDirectories) |> Array.toList
            else [])
        |> List.filter (fun p -> not (p.Contains "/obj/") && not (p.Contains "/bin/"))
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    let relativize (p: string) = Path.GetRelativePath(root, p)

    // Cached scan — the original scans each source twice, once for claims and once
    // for blocks. Doing it once cannot change the answer and halves the file reads.
    let scanned =
        sources
        |> List.map (fun f ->
            let lines = splitLines (slurp f)
            (f, lines, scanFile lines))

    let claims =
        [ for (f, lines, v) in scanned do
            let linesArr = List.toArray lines
            for i in 0 .. linesArr.Length - 1 do
                for own in v.[i].Spans |> List.filter (fun s -> s.Contains "MEASURED") do
                    // A TRAILING claim — code on its own line — gets NO neighbours at
                    // all: its receipt must sit in its own span.
                    let expand = not v.[i].Code
                    let start =
                        if not expand then i
                        else
                            let mutable k = i
                            while k > 0 && fullComment v.[k - 1] do k <- k - 1
                            k
                    let stop =
                        if not expand then i
                        else
                            let mutable k = i
                            while k + 1 < v.Length && fullComment v.[k + 1] do k <- k + 1
                            k
                    // COMMENT TEXT only. Including whole lines let a receipt named in
                    // adjacent CODE satisfy a claim.
                    let neighbours =
                        [ for j in start .. i - 1 -> v.[j] ] @ [ for j in i + 1 .. stop -> v.[j] ]
                    let block =
                        String.Join(" ", (neighbours |> List.collect (fun s -> s.Spans)) @ [ own ])
                    // `\Q...\E` is java-only; `Regex.Escape` is the .NET equivalent.
                    let named =
                        citable
                        |> Set.filter (fun c ->
                            Regex.IsMatch(block, "(?<![A-Za-z0-9_-])" + Regex.Escape c + "(?![A-Za-z0-9_-])"))
                    let unproven = block.Contains "UNPROVEN"
                    let line = linesArr.[i]
                    yield {| File = relativize f
                             Line = i + 1
                             Backed = (not (Set.isEmpty named)) || unproven
                             Unproven = unproven
                             Text = javaTrim (line.Substring(0, min 100 line.Length)) |} ]

    let findings = claims |> List.filter (fun c -> not c.Backed)
    let unprovenCount = claims |> List.filter (fun c -> c.Unproven) |> List.length

    // SECOND CHECK: A CITATION MUST NAME SOMETHING THAT EXISTS.
    // A CITATION IS BACKTICKED, OR INTRODUCED BY A COLON. Bare prose after the word
    // "receipt" is not a citation and must not be read as one.
    let citeBackticked = javaRx "(?i)receipts?\\s+((?:`[^`\n]+`(?:\\s*(?:,|and|or)\\s*)?)+)"
    let citeColon = javaRx "(?i)receipts?:\\s*((?:`?[a-z][a-z0-9./*-]*`?(?:\\s*(?:,|and|or)\\s*)?)+)"
    let citeName = javaRx "[a-z][a-z0-9]*(?:-[a-z0-9]+)*-?\\*|[a-z][a-z0-9]*(?:-[a-z0-9]+)+(?:[./]+[a-z0-9]+)*"
    let buildSuffixRx = javaRx "\\.b\\d+$"
    let trailingStarRx = javaRx "\\*$"
    let receiptExtRx = javaRx "\\.receipt\\.txt$"

    // A compact citation names SEVERAL receipts: `multi-case.b1/.b9` is `.b1` AND
    // `.b9` on one base. Expanding here means the resolver answers for one name at a
    // time and cannot silently check only the first.
    let expandCitation (tok: string) =
        let parts = tok.Split('/') |> Array.toList
        if List.length parts < 2 then [ tok ]
        else
            let first = List.head parts
            let bas = buildSuffixRx.Replace(first, "")
            first :: (List.tail parts |> List.map (fun p -> if p.StartsWith "." then bas + p else p))

    let resolves (tok: string) =
        let isGlob = tok.EndsWith "*"
        let t = receiptExtRx.Replace(trailingStarRx.Replace(tok, ""), "")
        let explicitBuild = buildSuffixRx.IsMatch t
        if isGlob then citable |> Set.exists (fun c -> c.StartsWith(t, StringComparison.Ordinal))
        elif explicitBuild then citable.Contains t
        else
            citable.Contains t
            || (citable |> Set.exists (fun c -> c.StartsWith(t + ".", StringComparison.Ordinal)))

    // A block is a run of FULL-LINE comments, reusing the same `fullComment` the
    // claims check uses so the two cannot drift.
    let blocks =
        [ for (f, lines, v) in scanned do
            let n = v.Length
            for start in 0 .. (List.length lines) - 1 do
                if not (List.isEmpty v.[start].Spans)
                   // `start = 0` FIRST — `v.[-1]` throws.
                   && (start = 0 || v.[start].Code || not (fullComment v.[start - 1])) then
                    let stop =
                        if v.[start].Code then start
                        else
                            let mutable k = start
                            while k + 1 < n && fullComment v.[k + 1] do k <- k + 1
                            k
                    let text =
                        String.Join(" ", [ for j in start .. stop do yield! v.[j].Spans ])
                    yield (f, start + 1, text) ]

    let dangling =
        [ for (f, line, text) in blocks do
            let lists =
                [ for m in citeBackticked.Matches text -> m.Groups.[1].Value ]
                @ [ for m in citeColon.Matches text -> m.Groups.[1].Value ]
            for lst in lists do
                for tokM in citeName.Matches lst do
                    for name in expandCitation tokM.Value do
                        if not (resolves name) then yield (relativize f, line, name) ]

    out (String.Format("MEASURED claims: {0} source files scanned, {1} receipts + {2} lane scenarios + {3} proof cases citable",
                       List.length sources, Set.count receipts, Set.count laneScenarios, Set.count proofCases))
    if List.isEmpty findings then
        out (String.Format("every MEASURED claim resolves: cited by a receipt, or admitted UNPROVEN ({0})", unprovenCount))
    else
        out (String.Format("\n{0} claim(s) neither cite a receipt nor admit UNPROVEN ({1} admitted):\n",
                           List.length findings, unprovenCount))
        for c in findings do
            out (String.Format("  {0}:{1}\n    {2}", c.File, c.Line, c.Text))
        out "\nEach must either cite its receipt, or say it is UNPROVEN."
    if List.isEmpty dangling then
        out "every receipt CITATION resolves to a receipt, lane scenario or proof case that exists"
    else
        out (String.Format("\n{0} citation(s) name something that does not exist:\n", List.length dangling))
        for (f, line, name) in dangling do
            out (String.Format("  {0}:{1}\n    cites `{2}`, which is not a receipt, lane scenario or proof case", f, line, name))
        out "\nEither write the receipt, or stop citing it."
    if strict && (not (List.isEmpty findings) || not (List.isEmpty dangling)) then exitWith 1
    0
