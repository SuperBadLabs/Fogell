#load "prelude.fsx"
/// FG-053. Count `options` DIRECTIVES across the corpus, by directive and by
/// scope, because a file count hides how cheap most of the work is and a naive
/// `rg -l` over-counts: the very first sampled file carries `//retry(3)`,
/// commented out, which a string search reports as a retry user.
///
/// Scans with a tiny lexer rather than regexes: LINE AND BLOCK COMMENTS are
/// skipped — strings are NOT. Blanking strings is what broke the first version:
/// one unmatched apostrophe swallowed the rest of a file and 33 files with an
/// options block were reported as 10.
///
/// STATED CONSEQUENCE: an `options { ... }` block written INSIDE a Groovy string
/// — a triple-quoted heredoc generating a Jenkinsfile, say — is counted as real.
/// None appears in this corpus, and the adjacent-brace rule below makes a quoted
/// bare word `options` harmless, but a genuine block in a string would land. Then
/// `options` blocks are found by brace matching and their directives read off. A
/// `stage {` seen at an enclosing depth marks the block STAGE-LEVEL — which
/// matters now that FG-120 refuses that form.
///
/// Ported from `count-options.bb` under FG-226.
open System
open System.Text
open Prelude

/// Blank out COMMENTS ONLY, preserving offsets so brace matching stays aligned.
///
/// An earlier version also blanked string literals, and one unmatched quote — an
/// apostrophe in prose — blanked the REST OF THE FILE, so 33 files with an
/// options block were reported as 10. Strings are therefore left alone, and the
/// cost is stated in the header rather than argued away here.
///
/// `//` preceded by `:` is left alone, so a URL inside a string does not eat the
/// line.
let scrub (s: string) =
    let n = s.Length
    let sb = StringBuilder(n)
    let mutable i = 0
    while i < n do
        let c = s.[i]
        let nx = if i + 1 < n then Some s.[i + 1] else None
        let prv = if i > 0 then Some s.[i - 1] else None
        if c = '/' && nx = Some '/' && prv <> Some ':' then
            let e = match s.IndexOf('\n', i) with | -1 -> n | j -> j
            sb.Append(String(' ', e - i)) |> ignore
            i <- e
        elif c = '/' && nx = Some '*' then
            let e = match s.IndexOf("*/", i + 2, StringComparison.Ordinal) with | -1 -> n | j -> j + 2
            sb.Append(String(' ', e - i)) |> ignore
            i <- e
        else
            sb.Append(c) |> ignore
            i <- i + 1
    sb.ToString()

/// A Groovy identifier character. `isLetterOrDigit` alone treats `_` and `$` as
/// boundaries, so `my_stage('x') { options { retry(2) } }` was read as a
/// Declarative stage and reported a stage-level `retry` that is not one.
let identChar (c: char) = Char.IsLetterOrDigit c || c = '_' || c = '$'

let startsAt (s: string) (i: int) (word: string) =
    i + word.Length <= s.Length && String.CompareOrdinal(s, i, word, 0, word.Length) = 0

/// (start, end, stageLevel) for every `options { ... }` block.
let optionsBlocks (s: string) =
    let n = s.Length
    let acc = ResizeArray<int * int * bool>()
    let mutable i = 0
    let mutable depth = 0
    let mutable stageDepths = Set.empty<int>
    while i < n do
        let c = s.[i]
        if c = '{' then
            depth <- depth + 1
            i <- i + 1
        elif c = '}' then
            stageDepths <- Set.remove depth stageDepths
            depth <- depth - 1
            i <- i + 1
        // `stage` on a WORD BOUNDARY, then optional whitespace and `(` or `{`.
        // Matching the literal "stage " missed `stage('x')` — the form
        // Declarative actually uses — so every stage-level options block was
        // reported as pipeline-level.
        elif c = 's' && startsAt s i "stage"
             && (i = 0 || not (identChar s.[i - 1]))
             && (let mutable k = i + 5
                 while k < n && javaIsWhitespace s.[k] do k <- k + 1
                 k < n && (s.[k] = '(' || s.[k] = '{')) then
            stageDepths <- Set.add (depth + 1) stageDepths
            i <- i + 1
        elif c = 'o' && startsAt s i "options" && (i = 0 || not (identChar s.[i - 1])) then
            // the brace must be the NEXT non-whitespace character. `index-of`
            // took the next `{` ANYWHERE, so a quoted `options` — scrub leaves
            // strings intact — started a block at some distant brace and swept
            // unrelated code in.
            let mutable j = i + 7
            while j < n && javaIsWhitespace s.[j] do j <- j + 1
            if j < n && s.[j] = '{' then
                let ob = j
                let mutable k = ob
                let mutable d = 0
                let mutable closeAt = -1
                while closeAt < 0 && k < n do
                    if s.[k] = '{' then d <- d + 1; k <- k + 1
                    elif s.[k] = '}' then
                        if d = 1 then closeAt <- k else (d <- d - 1; k <- k + 1)
                    else k <- k + 1
                let closeAt = if closeAt < 0 then n else closeAt
                acc.Add(ob + 1, closeAt, not (Set.isEmpty stageDepths))
                i <- closeAt + 1
            else i <- i + 1
        else i <- i + 1
    List.ofSeq acc

/// Directive = the identifier opening a STATEMENT inside the block, depth 0 only.
/// A line-leading regex took the FIRST name on a line — so
/// `options { timestamps(); timeout(...) }` lost the second — and counted
/// continuation lines of a multiline argument, so
/// `buildDiscarder(logRotator(\n numToKeepStr: '5'))` reported `numToKeepStr`.
let directivesIn (body: string) =
    let m = body.Length
    let acc = ResizeArray<string>()
    let mutable k = 0
    let mutable depth = 0
    let mutable atStart = true
    while k < m do
        let c = body.[k]
        if c = '(' then depth <- depth + 1; atStart <- false; k <- k + 1
        elif c = ')' then depth <- max 0 (depth - 1); atStart <- false; k <- k + 1
        elif c = '\n' || c = ';' || c = '{' || c = '}' then atStart <- true; k <- k + 1
        elif javaIsWhitespace c then k <- k + 1
        elif atStart && depth = 0 && Char.IsLetter c then
            let mutable q = k
            while q < m && (Char.IsLetterOrDigit body.[q] || body.[q] = '_') do q <- q + 1
            acc.Add(body.Substring(k, q - k))
            depth <- depth
            atStart <- false
            k <- q
        else atStart <- false; k <- k + 1
    // `distinct` preserving first-seen order, as Clojure's `distinct` does
    let seen = System.Collections.Generic.HashSet<string>()
    acc |> Seq.filter seen.Add |> List.ofSeq

type Row = { File: string; Directive: string; Stage: bool }

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        out "usage: count-options <corpus-directory>"
        exitWith 2
    let files =
        IO.Directory.GetFiles(argv.[0])
        |> Array.toList
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
    let rows =
        [ for f in files do
            let s = scrub (slurp f)
            for (a, b, stage) in optionsBlocks s do
                for d in directivesIn (s.Substring(a, b - a)) do
                    yield { File = IO.Path.GetFileName f; Directive = d; Stage = stage } ]
    let distinctFiles (rs: Row list) =
        rs |> List.map (fun r -> r.File) |> List.distinct |> List.length
    out (String.Format("corpus files: {0}   files with an options block: {1}",
                       List.length files, distinctFiles rows))
    out ""
    out (String.Format("{0,-28} {1,8} {2,8} {3,8}", "directive", "files", "pipeline", "stage"))
    // THE ONE DELIBERATE DEVIATION FROM THE BABASHKA ORIGINAL, stated rather
    // than hidden. Clojure's `group-by` returns a PersistentHashMap once it
    // holds more than eight keys, and `sort-by` is stable, so directives with
    // EQUAL counts came out in hash-bucket order — an accident of the hash
    // implementation, not a decision. Reproducing that accident would mean
    // hard-coding Clojure's `hasheq` and HAMT traversal into this tool. Ties
    // are therefore broken by directive name, which is stable across inputs and
    // hosts. Counts, columns and row membership are byte-identical to the
    // original; only the relative order of equal-count rows differs, and the
    // rows that relocate on the pinned corpus are TWO: `timestamps` inside the
    // count-14 tie and `skipStagesAfterUnstable` inside the count-1 tie. This
    // comment said three rows and said they were all count-1; both were wrong.
    rows
    |> List.groupBy (fun r -> r.Directive)
    |> List.sortWith (fun (da, a) (db, b) ->
        match compare (List.length b) (List.length a) with
        | 0 -> String.CompareOrdinal(da, db)
        | c -> c)
    |> List.iter (fun (d, rs) ->
        out (String.Format("{0,-28} {1,8} {2,8} {3,8}",
                           d,
                           distinctFiles rs,
                           distinctFiles (rs |> List.filter (fun r -> not r.Stage)),
                           distinctFiles (rs |> List.filter (fun r -> r.Stage)))))
    0
