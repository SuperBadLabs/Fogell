#load "prelude.fsx"
/// FG-104b. A comment that names a mechanism the code no longer has.
///
/// `audit-claims` cannot see this class. It asks whether a MEASURED claim names a
/// receipt — a different question entirely, and one a stale identifier passes
/// trivially.
///
/// The check: for every F# BINDING of four or more characters this diff DELETED
/// — `let`/`member`/`type`/`override`/`default`/`and` declarations and PascalCase
/// record fields — is it still named in a comment that survived? Line comments and
/// NESTED `(* ... *)` blocks both.
///
/// F# BINDINGS, not identifiers in general. A deleted shell function, a `bb` def, a
/// YAML key or a step name in a lane script is NOT extracted, so a comment naming
/// one survives this audit silently. Comments in those files ARE searched — the gap
/// is what gets collected from the diff, not where it looks.
///
/// The length floor is a real limit: `x`, `i`, `id`, `ctx` occur inside ordinary
/// English in comments, and this checker's whole value is that a report means
/// something. A gate that cries wolf on prose is a gate someone turns off.
///
///   usage: scripts/bin/audit-stale-refs [base-ref]     (default origin/main)
///          --strict exits non-zero on any surviving reference
///
/// Ported from `audit-stale-refs.bb` under FG-226. Two translation notes:
/// `\Q...\E` is java-only and becomes `Regex.Escape`; and the original walked the
/// tree in filesystem order, which made the ORDER of its report host-dependent.
/// Files and identifiers are now ordinally sorted, so the same tree reports the
/// same lines in the same order everywhere. Membership is unchanged.
open System
open System.IO
open System.Text.RegularExpressions
open Prelude

type Mode =
    | ModeCode
    | ModeStr
    | ModeVStr
    | ModeTStr

/// Length of the F# character literal starting at i, or None if this apostrophe is
/// something else — a generic type parameter (`'T`), or the tail of an identifier
/// (`state'`). Recognised only when a closing quote actually follows.
let charLiteralLen (l: string) (i: int) (n: int) =
    if i >= n || l.[i] <> '\'' then None
    elif i + 1 < n && l.[i + 1] = '\\' then
        let limit = min n (i + 10)
        let mutable j = i + 2
        let mutable found = -1
        while found < 0 && j < limit do
            if l.[j] = '\'' then found <- j else j <- j + 1
        if found >= 0 then Some(found + 1 - i) else None
    elif i + 2 < n && l.[i + 2] = '\'' then Some 3
    else None

/// (comment-start, depth', mode', code?, code-start) for one line.
///
/// A CHARACTER SCAN carrying state ACROSS lines. Resetting the string mode per
/// line is simply false about this repository: Store.fs opens an ordinary
/// multi-line string whose continuation lines contain `count(*)`. Scanned as code,
/// that `(*` opens a block comment that never closes, every later line reads as
/// comment text, and its declarations are filtered out of the definition scan.
let scanLine (l: string) (depth: int) (mode: Mode) =
    let n = l.Length
    let atStr (i: int) (t: string) =
        i + t.Length <= n && String.CompareOrdinal(l, i, t, 0, t.Length) = 0
    let mutable i = 0
    let mutable d = depth
    let mutable m = mode
    let mutable start = if depth > 0 && mode = ModeCode then Some 0 else None
    let mutable code = false
    let mutable codeStart: int option = None
    let mutable stop = false
    let markStart (k: int) = if start.IsNone then start <- Some k
    let markCode (k: int) = if codeStart.IsNone then codeStart <- Some k

    while not stop && i < n do
        let c = l.[i]
        if d > 0 then
            // inside a block comment: only its own delimiters matter, and F# block
            // comments NEST
            markStart i
            if atStr i "*)" then (d <- d - 1; i <- i + 2)
            elif atStr i "(*" then (d <- d + 1; i <- i + 2)
            else i <- i + 1
        elif m = ModeTStr then
            if atStr i "\"\"\"" then (m <- ModeCode; i <- i + 3) else i <- i + 1
        elif m = ModeVStr then
            if atStr i "\"\"" then i <- i + 2
            elif c = '"' then (m <- ModeCode; i <- i + 1)
            else i <- i + 1
        elif m = ModeStr then
            if c = '\\' && i + 1 < n then i <- i + 2
            elif c = '"' then (m <- ModeCode; i <- i + 1)
            else i <- i + 1
        else
            // CHARACTER LITERALS FIRST. `'"'` is a live shape here and its quote is
            // not a string delimiter. Taking it as one leaves the scanner in a
            // string for the rest of the file, dropping every later comment out of
            // the index — so a deleted binding named by one passes --strict silently.
            match charLiteralLen l i n with
            | Some len ->
                code <- true; markCode i; i <- i + len
            | None ->
                if atStr i "\"\"\"" then (code <- true; markCode i; m <- ModeTStr; i <- i + 3)
                elif atStr i "@\"" then (code <- true; markCode i; m <- ModeVStr; i <- i + 2)
                elif c = '"' then (code <- true; markCode i; m <- ModeStr; i <- i + 1)
                elif atStr i "//" then (markStart i; stop <- true)
                elif atStr i ";;" then (markStart i; stop <- true)
                elif c = '#' then (markStart i; stop <- true)
                elif atStr i "(*" then (markStart i; d <- d + 1; i <- i + 2)
                else
                    if not (javaIsWhitespace c) then
                        code <- true
                        markCode i
                    i <- i + 1
    (start, d, m, code, codeStart)

/// (line-no, text, whole?) for every line that is, or begins, a comment.
///
/// `carry` threads lexical state between lines, correct for F# and deliberately
/// NOT done elsewhere: an unbalanced quote in a shell script would otherwise
/// swallow the rest of the file.
let commentSpans (lines: string list) (carry: bool) =
    let acc = ResizeArray<int * string * bool>()
    let mutable depth = 0
    let mutable mode = ModeCode
    let mutable n = 1
    for l in lines do
        let inside = depth > 0 && mode = ModeCode
        let (idx, depth', mode', _, _) = scanLine l depth mode
        let whole =
            inside || (match idx with
                       | Some ix -> blank (l.Substring(0, ix))
                       | None -> false)
        match idx with
        | Some ix -> acc.Add(n, l.Substring ix, whole)
        | None -> ()
        depth <- (if carry then depth' else 0)
        mode <- (if carry then mode' else ModeCode)
        n <- n + 1
    List.ofSeq acc

/// Line numbers mapped to source beginning at their first token outside a carried
/// string or block/line comment.
let codeLineProjections (lines: string list) =
    let acc = System.Collections.Generic.Dictionary<int, string>()
    let mutable depth = 0
    let mutable mode = ModeCode
    let mutable n = 1
    for l in lines do
        let (_, depth', mode', code, codeStart) = scanLine l depth mode
        if code then
            match codeStart with
            | Some cs -> acc.[n] <- l.Substring cs
            | None -> ()
        depth <- depth'
        mode <- mode'
        n <- n + 1
    acc

// An F# identifier may END in `'`, which `\b` cannot close against. Rust's regex
// engine has no lookaround, so the boundary is an explicit class plus end-of-line.
let fsharpBoundary = "($|[^A-Za-z0-9_'])"

// THE TWO PATTERNS ARE BUILT FROM THESE, not written twice. They had drifted
// twice already. Two regexes describing one grammar will diverge; sharing the
// pieces makes it structural rather than a thing to remember.
let preKw = "static|abstract|override|default|and"
let kw = "let|member|type|override|default|and|use"
let mods = "private|internal|public|mutable|rec|inline|new|val"

/// EVERY GROUP IS NON-CAPTURING. The extractor keeps all capture groups, so
/// `member`, `let` and the modifiers would themselves be collected as deleted
/// identifiers — a false positive that BLOCKS pushes.
let bindingCore =
    "(?:(?:" + preKw + ")\\s+)*(?:" + kw + ")!?\\s+(?:(?:" + mods + ")\\s+)*(?:[A-Za-z_][A-Za-z0-9_']*\\.)?"

/// A record field, which this codebase writes indented, on the brace line,
/// carrying an attribute, or `mutable`.
let fieldLead = "\\{?\\s*(?:\\[<[^>]*>\\]\\s*)?(?:mutable\\s+)?"

/// These are literal patterns, never binders.
let patternLiterals = set [ "true"; "false"; "null" ]

/// The LEFT boundary is load-bearing: without it `Choice` is entered at its second
/// character and `hoice` becomes a fictitious lowercase binder.
let destructuredToken = javaRx "(?:^|[^A-Za-z0-9_'])(_+[A-Za-z0-9][A-Za-z0-9_']*|[a-z][A-Za-z0-9_']{3,})"

let definitionParenRx =
    javaRx "^(?:\\(\\s*[!%&*+\\-./<=>?@^|~:]+\\s*\\)|\\(\\s*\\|(?:[A-Z][A-Za-z0-9_']*\\|)+(?:_\\|)?\\s*\\))\\s+[^,=\\s]"

/// True when a parenthesised token after `let` is an active-pattern or operator
/// DEFINITION header with a real following parameter, rather than a destructuring
/// pattern.
let definitionParenForm (t: string) = definitionParenRx.IsMatch(javaTrim t)

let maxDepth (t: string) =
    let mutable d = 0
    let mutable mx = 0
    for c in t do
        if c = '(' then (d <- d + 1; mx <- max mx d)
        elif c = ')' then d <- max 0 (d - 1)
    mx

let fsExtRx = javaRx "\\.(fs|fsi|fsx)$"
let hunkRx = javaRx "^@@ -(\\d+)(?:,\\d+)? \\+\\d+(?:,\\d+)? @@"
let skipDirRx = javaRx "/(bin|obj|\\.git)/"

let die (msg: string) =
    out msg
    exitWith 2

/// Removed F# code projections (with the diff's leading minus), excluding lines
/// wholly inside comments or carried strings in the BASE blob.
let removedFsharpLines (baseRef: string) (diff: string) =
    let removed = ResizeArray<string * int * string>()
    let mutable file: string = null
    let mutable oldLine = 0
    let mutable haveLine = false
    let mutable inHunk = false
    for l in splitLines diff do
        // In a hunk, prefixes describe content. Test these BEFORE file headers so
        // deleted source beginning with two dashes cannot impersonate a header.
        if inHunk && l.StartsWith("-", StringComparison.Ordinal) then
            if not (isNull file) && fsExtRx.IsMatch file && haveLine then
                removed.Add(file, oldLine, l)
            oldLine <- oldLine + 1
        elif inHunk && l.StartsWith("+", StringComparison.Ordinal) then ()
        elif inHunk && l.StartsWith(" ", StringComparison.Ordinal) then oldLine <- oldLine + 1
        elif inHunk && l = "\\ No newline at end of file" then ()
        elif l.StartsWith("--- ", StringComparison.Ordinal) then
            let p = l.Substring 4
            file <- (if p.StartsWith("a/", StringComparison.Ordinal) then p.Substring 2 else null)
            inHunk <- false
            haveLine <- false
        else
            let m = hunkRx.Match l
            if m.Success then
                oldLine <- int m.Groups.[1].Value
                haveLine <- true
                inHunk <- true
            else inHunk <- false

    let files = removed |> Seq.map (fun (f, _, _) -> f) |> Seq.distinct |> List.ofSeq
    let projections =
        files
        |> List.map (fun f ->
            let r = run "git" [ "show"; baseRef + ":" + f ]
            if not (runOk r) then
                die (String.Format("stale-reference audit: cannot read base blob {0}:{1}: {2}",
                                   baseRef, f, javaTrim r.Err))
            (f, codeLineProjections (splitLines r.Out)))
        |> dict

    [ for (f, n, raw) in removed do
        match projections.TryGetValue f with
        | true, proj ->
            // The projection is consulted rather than the diff text: an interior
            // line of a multi-line block comment looks like code once a -U0 hunk
            // has thrown its lexical state away. Falling back to `raw` here is the
            // planted mutation `prove-stale-refs` kills.
            let projected = match proj.TryGetValue n with | true, code -> Some code | _ -> None
            match projected with
            | Some code -> yield "-" + code
            | None -> ()
        | _ -> () ]

[<EntryPoint>]
let main argv =
    let args = List.ofArray argv
    let strict = args |> List.contains "--strict"
    let baseRef =
        args |> List.filter (fun a -> a <> "--strict") |> List.tryHead |> Option.defaultValue "origin/main"

    // THE BASE MUST RESOLVE. This script shipped without checking it: a bad
    // revision went to stderr, stdout came back empty, and the audit reported
    // "nothing stale" and exited 0 — a BLOCKING gate turned green by a typo.
    if not (runOk (run "git" [ "rev-parse"; "--verify"; "--quiet"; baseRef + "^{commit}" ])) then
        die (String.Format("stale-reference audit: base ref {0} does not resolve — refusing to report a clean tree it never compared against", baseRef))

    let roots = [ "src"; "tools"; "scripts"; "tests" ] |> List.filter Directory.Exists

    let dr = run "git" ([ "diff"; "--no-ext-diff"; "--no-renames"; "-U0"; baseRef; "--" ] @ roots)
    if not (runOk dr) then
        die (String.Format("stale-reference audit: `git diff {0}` failed: {1}", baseRef, javaTrim dr.Err))
    let diff = dr.Out

    let fsharpRemoved = removedFsharpLines baseRef diff

    // ANCHORED to the start of the removed line, which settles a false POSITIVE:
    // deleting a COMMENT containing binding syntax was extracted as a deleted
    // binding, so removing stale documentation could fail the build.
    let removedRx =
        javaRx ("^\\-\\s*" + bindingCore + "([A-Za-z][A-Za-z0-9_']{3,})|^\\-\\s*" + fieldLead + "([A-Z][A-Za-z0-9_']{3,})\\s*:")
    let removedSet =
        fsharpRemoved
        |> List.collect (fun l ->
            [ for m in removedRx.Matches l do
                for gi in 1 .. m.Groups.Count - 1 do
                    let g = m.Groups.[gi]
                    if g.Success && g.Value <> "" then yield g.Value ])
        |> Set.ofList

    // DESTRUCTURING PATTERNS. The arms above want a NAME straight after the
    // keyword, so `let (verdict, folds), j, f =` contributed nothing.
    let destructuredRx = javaRx ("^\\-\\s*" + bindingCore + "(\\(.*?)=")
    let destructured =
        fsharpRemoved
        |> List.choose (fun l ->
            let m = destructuredRx.Match l
            if m.Success then Some m.Groups.[1].Value else None)
        // An active-pattern or parenthesised operator here is a DEFINITION.
        |> List.filter (fun t -> not (definitionParenForm t))
        // depth > 1 is a NESTED pattern and stays uncovered — consistently.
        |> List.filter (fun t -> maxDepth t <= 1)
        // A pattern holding a STRING OR CHARACTER LITERAL is skipped whole; `'`
        // alone is NOT a literal marker, since F# identifiers end in one.
        |> List.filter (fun t ->
            not (t.Contains "\"")
            && not (Seq.exists (fun i -> (charLiteralLen t i t.Length).IsSome) (seq { 0 .. t.Length - 1 })))
        |> List.filter (fun t -> not (t.Contains ":"))
        // LOWERCASE INITIAL ONLY: inside a pattern an UPPERCASE identifier is a
        // union case or literal and therefore never a binder.
        |> List.collect (fun t -> [ for m in destructuredToken.Matches t -> m.Groups.[1].Value ])
        // The documented floor is the COMPLETE identifier's length.
        |> List.filter (fun s -> s.Length >= 4)
        |> List.filter (fun s -> not (patternLiterals.Contains s))
        |> Set.ofList

    let removedAll = Set.union removedSet destructured

    // Only the roots that EXIST — a scratch tree otherwise fills the report with
    // rg errors that can hide a real one.
    let scanned =
        roots
        |> List.collect (fun r ->
            Directory.GetFiles(r, "*", SearchOption.AllDirectories)
            |> Array.toList)
        |> List.filter (fun p -> not (skipDirRx.IsMatch("/" + p.Replace("\\", "/"))))
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.choose (fun p ->
            try Some(p, commentSpans (splitLines (File.ReadAllText p)) (fsExtRx.IsMatch p))
            with _ -> None)

    let commentIndex =
        [ for (path, spans) in scanned do
            for (n, text, _) in spans -> (path, n, text) ]

    // Line numbers that are ENTIRELY comment, per file. One comment model for both
    // scans, rather than two that disagree at the edges.
    let wholeComment =
        scanned
        |> List.map (fun (path, spans) ->
            (path, spans |> List.choose (fun (n, _, whole) -> if whole then Some n else None) |> Set.ofList))
        |> dict

    // rg exits 0 on a match, 1 on none, and >1 on a REAL ERROR. Ignoring the code
    // made every error read as "no match" — a blocking checker that cannot search
    // must say CANNOT PROVE.
    let rgRun (what: string) (rgArgs: string list) =
        let r = run "rg" rgArgs
        if r.Rc > 1 then
            die (String.Format("stale-reference audit: rg failed while {0} (exit {1}): {2}",
                               what, r.Rc, javaTrim r.Err))
        r.Out

    let searchRoots = roots |> List.filter (fun r -> r <> "scripts")

    // A COMMENT IS NOT A DEFINITION. The pattern is ANCHORED: a surviving STRING
    // — `let keep = "let StaleGateValue"` — read as a definition, and the
    // whole-line comment filter cannot help because the line is code.
    let stillDefined (id: string) =
        let pattern =
            "^\\s*" + bindingCore + id + fsharpBoundary
            + "|^\\s*" + fieldLead + id + "\\s*:"
            + "|^\\s*" + bindingCore + "\\([^=]*" + id + fsharpBoundary
        let outp = rgRun ("searching for a surviving definition of " + id)
                         ([ "-n"; "--no-heading"; pattern ] @ searchRoots)
        splitLines outp
        |> List.filter (fun l -> not (blank l))
        // rg -n --no-heading prints `path:line:content`
        |> List.choose (fun l ->
            let parts = l.Split([| ':' |], 3)
            if parts.Length = 3 then
                match Int32.TryParse parts.[1] with
                | true, n -> Some(parts.[0], n)
                | _ -> None
            else None)
        |> List.filter (fun (path, n) ->
            match wholeComment.TryGetValue path with
            | true, s -> not (s.Contains n)
            | _ -> true)
        |> List.isEmpty
        |> not

    // SEARCHED IN PARALLEL, JUDGED IN ORDER. `stillDefined` spawns one `rg` per
    // candidate identifier and there are typically well over a hundred, so the
    // audit spent most of its wall time waiting on process startup rather than on
    // searching. The queries are independent and pure — each asks only whether one
    // name survives — so they are answered concurrently and the verdicts are then
    // read back in the sorted order the report uses. Parallelism is confined to
    // the LOOKUP; nothing about which identifiers are reported, or in what order,
    // depends on scheduling.
    let candidates =
        removedAll
        |> Set.toArray
        |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
    let verdicts = System.Collections.Concurrent.ConcurrentDictionary<string, bool>()
    System.Threading.Tasks.Parallel.ForEach(
        candidates, fun (id: string) -> verdicts.[id] <- stillDefined id) |> ignore
    let gone =
        candidates
        |> Array.filter (fun id -> not verdicts.[id])
        |> Array.toList

    // ...but still named in a surviving COMMENT. Built from the files themselves,
    // because `rg` cannot carry block-comment depth across lines.
    // `\Q...\E` is java-only; `Regex.Escape` is the .NET equivalent.
    let hits =
        [ for id in gone do
            let pat = Regex("\\b" + Regex.Escape id + javaPattern fsharpBoundary)
            for (path, n, text) in commentIndex do
                if pat.IsMatch text then
                    yield (id, String.Format("{0}:{1}:{2}", path, n, javaTrim text)) ]

    out (String.Format("stale-reference audit: {0} identifier(s) removed vs {1}, {2} fully gone",
                       Set.count removedAll, baseRef, List.length gone))
    if List.isEmpty hits then
        out "no surviving comment names a deleted identifier"
    else
        out (String.Format("\n{0} comment(s) name an identifier this diff deleted:\n", List.length hits))
        for (id, line) in hits do
            out ("  " + padR 28 id + " " + javaTrim line)
        out "\nEach is either a comment to update or a deletion to reconsider."
        if strict then exitWith 1
    0
