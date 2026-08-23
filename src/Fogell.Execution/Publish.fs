namespace Fogell.Execution

open System
open System.IO
open System.Text.RegularExpressions

/// Where published artifacts and test results are collected. Kept separate from
/// the workspace so archiving is observable without polluting what the next step
/// sees — and so the differential's workspace hash is not perturbed by the act
/// of archiving.
type ArtifactStore =
    { Root: string }

    static member under(root: string) = { Root = root }

/// FG-042 / FG-043. Artifact archiving and test-result ingest.
///
/// Both are *publishing* operations: they read the workspace and record
/// something durable elsewhere. Neither mutates the workspace, which is what
/// keeps the differential's workspace hash meaningful.
/// FG-043. Why a test-report read did not produce counts. REVIEW FIX (Codex, PR #14
/// round 10): an interruption was returned as a plain `Error`, so the caller mapped it
/// to Failure and a `timeout` ending in `junit` selected `post { failure }` instead of
/// `post { aborted }` — unlike shell and archive timeouts. The cause has to survive.
type JUnitProblem =
    | Interrupted
    | NoReports
    | MissingTestName of report: string
    | MissingIdentity
    | Unreadable of string

module JUnitDiagnostics =

    /// FG-212. Literal line captured from the pinned Jenkins/JUnit oracle. The
    /// enhanced-NPE local name belongs to that exact runtime build.
    [<Literal>]
    let MissingTestNameMessage =
        "Cannot invoke \"String.contains(java.lang.CharSequence)\" because \"nameAttr\" is null"

/// FG-047. Controller-side stash storage.
///
/// Jenkins keeps a stash with the BUILD, not in the workspace, which is what makes it
/// survive `deleteDir()` — measured in the behavioural spec. Storing it under the
/// workspace would pass a naive test and fail the one that matters.
type StashStore =
    { Root: string }

    static member under (root: string) = { Root = root }

module Publish =

    type private MissingJUnitTestNameException() =
        inherit IOException("JUnit testcase has no name attribute or class fallback")

    /// Expand a Jenkins-style ant glob (`**/*.jar`, `target/*.txt`, `out.txt`)
    /// against a workspace. Deliberately supports only the forms measured in the
    /// corpus; anything else is reported rather than silently matching nothing.
    let expandGlob (workspace: string) (pattern: string) : string list =
        if not (Directory.Exists workspace) then
            []
        else
            let normalised = pattern.Replace('\\', '/').Trim()

            let regex =
                let escaped =
                    normalised.Split('/')
                    |> Array.map (fun segment ->
                        if segment = "**" then
                            "(?:.*)"
                        else
                            segment
                            |> Regex.Escape
                            |> fun e -> e.Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]"))
                    |> String.concat "/"
                    // `**/x` must also match a bare `x` at the root
                    |> fun p -> p.Replace("(?:.*)/", "(?:.*/)?")

                Regex("^" + escaped + "$", RegexOptions.IgnoreCase)

            Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
            |> Array.choose (fun full ->
                let relative = Path.GetRelativePath(workspace, full).Replace('\\', '/')
                if regex.IsMatch relative then Some relative else None)
            |> Array.sort
            |> Array.toList

    /// Copy matched files into the artifact store under `buildKey`, preserving
    /// relative layout. Returns the sorted relative paths actually published.
    /// `abort` is polled BETWEEN files.
    ///
    /// REVIEW FIX (Codex, PR #14): a `timeout` deadline only ever reached the shell
    /// runner, so an `archiveArtifacts` starting just before the deadline copied for
    /// as long as it liked while Jenkins would have aborted the block. Checking a
    /// predicate per file makes the deadline real for this step too. It is still not
    /// interruptible *within* a single large file copy, which is stated rather than
    /// implied.
    let archiveWithAbort (store: ArtifactStore) (buildKey: string) (workspace: string) (patterns: string list) (abort: unit -> bool) =
        let target = Path.Combine(store.Root, buildKey)

        let matched =
            patterns
            |> List.collect (expandGlob workspace)
            |> List.distinct
            |> List.sort

        let published = System.Collections.Generic.List<string>()

        // REVIEW FIX (Codex, PR #14 round 11): with `allowEmptyArchive: true` and no
        // matches, the copy loop never runs, so neither polling site was reached and an
        // interrupt during the (potentially long) glob scan left the step Successful.
        // Poll once after expansion, before the loop can decline to execute.
        let mutable aborted = abort ()

        for relative in matched do
            if not aborted then
                if abort () then
                    aborted <- true
                else
                    let dest = Path.Combine(target, relative)
                    Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
                    File.Copy(Path.Combine(workspace, relative), dest, true)
                    published.Add relative

                    // REVIEW FIX (Codex, PR #14 round 9): polling only BEFORE each copy
                    // meant an interrupt firing during the only or final copy had no
                    // later iteration to observe it, so `aborted` stayed false and the
                    // step returned Success — a timeout expiring while the build stays
                    // green. A copy cannot be interrupted mid-file, but once it returns
                    // the interruption must still be classified.
                    if abort () then aborted <- true

        List.ofSeq published, aborted

    let archive store buildKey workspace patterns =
        archiveWithAbort store buildKey workspace patterns (fun () -> false) |> fst

    /// Parse JUnit XML totals. The pinned plugin derives its summary from testcase
    /// children and ignores the suite aggregate attributes, which are frequently
    /// absent, stale, or producer-specific.
    /// The pinned JUnit plugin turns a syntactically malformed `.xml` report into
    /// one synthetic failed test (`[failed-to-read]`) and continues aggregating
    /// the other matched reports. Preserve that narrow compatibility rule while
    /// keeping nonzero-file open failures and malformed non-XML inputs unreadable.
    /// `abort` is polled between report files. REVIEW FIX (Codex, PR #14 round 9):
    /// StepRequest.DeadlineExpired was documented as polled by "archive, junit" and
    /// only archive read it, so a `timeout` whose last step is `junit` could scan many
    /// reports and return Success or Unstable after the deadline.
    let parseJUnitWithAbort
        (workspace: string)
        (patterns: string list)
        (abort: unit -> bool)
        : Result<int * int * int * single option, JUnitProblem> =
        let files =
            patterns
            |> List.collect (expandGlob workspace)
            |> List.distinct
            |> List.sort

        // REVIEW FIX (Codex, PR #14 round 12): the mirror of the archive zero-match
        // case. With no matching report the scan can still have been long, and an
        // interrupt firing during it was reported as "no test report matched" — a
        // pattern problem the user would go and debug — instead of an abort.
        if abort () then
            Error Interrupted
        elif List.isEmpty files then
            // Keep absence distinct from a report which matched but could not be
            // read. `allowEmptyResults` may permit this condition, but must never
            // suppress a genuine I/O or parse failure.
            Error NoReports
        else
            // Accumulate wider than the Java summary surface so neither a very large
            // child population nor synthetic failures can wrap into false success.
            let mutable total = 0L
            let mutable failed = 0L
            let mutable skipped = 0L
            // JUnit stores, parses, clamps, and adds durations as JVM float.
            // Keep every addition binary32: accumulating as double and narrowing
            // once changes observable summaries at the 2^24 boundary.
            let mutable duration = Some 0.0f
            let mutable immediateProblem: JUnitProblem option = None
            let mutable sawMissingIdentity = false

            let mutable aborted = false

            for relative in files do
              if not aborted && Option.isNone immediateProblem then
                if abort () then
                    aborted <- true
                else
                try
                    // Jenkins calls File.length() before opening or parsing. Java
                    // returns zero both for a zero-byte file and for a path which
                    // vanished after glob expansion, so both become one synthetic
                    // `[empty]` failure without an open attempt. A non-empty parse
                    // failure is recovered only for an exact lowercase `.xml` path.
                    let report = FileInfo(Path.Combine(workspace, relative))

                    if not report.Exists || report.Length = 0L then
                        total <- total + 1L
                        failed <- failed + 1L
                    else
                        use stream = report.OpenRead()
                        let doc = Xml.Linq.XDocument.Load(stream)

                        let directElements name (element: Xml.Linq.XElement) =
                            // dom4j's Element.elements(String)/element(String)
                            // compare the exact, case-sensitive local name. The
                            // namespace URI and prefix are not part of the match.
                            element.Elements()
                            |> Seq.filter (fun child -> child.Name.LocalName = name)

                        let firstAttribute name (element: Xml.Linq.XElement) =
                            // dom4j stores namespace declarations as Namespace
                            // nodes, not Attributes. LINQ exposes them as
                            // attributes, so exclude them before matching the
                            // same exact local-name contract in document order.
                            element.Attributes()
                            |> Seq.tryFind (fun attribute ->
                                not attribute.IsNamespaceDeclaration
                                && attribute.Name.LocalName = name)

                        let hasDirectChild name (element: Xml.Linq.XElement) =
                            directElements name element |> Seq.isEmpty |> not

                        let hasAttribute name (element: Xml.Linq.XElement) =
                            firstAttribute name element |> Option.isSome

                        let parseDuration (raw: string) =
                            // Pinned TimeToFloat removes commas, tries
                            // Float.parseFloat, then DecimalFormat.parse (which
                            // may accept a numeric prefix), and defaults to zero.
                            let javaWhitespace = [| for code in 0..32 -> char code |]
                            let cleaned = raw.Replace(",", "")
                            // Float.parseFloat accepts its documented leading and
                            // trailing ASCII whitespace. DecimalFormat.parse does
                            // not, so retain both views for the two-stage path.
                            let text = cleaned.Trim(javaWhitespace)
                            let styles = Globalization.NumberStyles.Float
                            let invariant = Globalization.CultureInfo.InvariantCulture

                            let normalizeDecimalDigits (value: string) =
                                // DecimalFormat accepts every Unicode Nd digit,
                                // whereas Float.parseFloat is ASCII-only. The pinned
                                // parser walks UTF-16 chars, not grapheme clusters or
                                // scalar Runes: map one BMP digit to one ASCII digit
                                // and preserve combining marks/surrogates verbatim.
                                value
                                |> Seq.map (fun codeUnit ->
                                    let digit = Globalization.CharUnicodeInfo.GetDecimalDigitValue codeUnit
                                    if digit >= 0 then char (int '0' + digit) else codeUnit)
                                |> Seq.toArray
                                |> String

                            let decimalToken =
                                Regex.IsMatch(
                                    text,
                                    @"^[+-]?(?:NaN|Infinity|(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?[fFdD]?)$"
                                )

                            let hexadecimalToken =
                                Regex.IsMatch(
                                    text,
                                    @"^[+-]?0[xX](?:[0-9a-fA-F]+(?:\.[0-9a-fA-F]*)?|\.[0-9a-fA-F]+)[pP][+-]?[0-9]+[fFdD]?$"
                                )

                            let parsed =
                                if hexadecimalToken then
                                    // Java accepts hexadecimal float literals. A
                                    // double-mediated conversion can double-round,
                                    // so keep counts available but refuse duration
                                    // access instead of guessing at binary32 edges.
                                    None
                                elif decimalToken then
                                    let core =
                                        if text.Length > 0 && "fFdD".Contains text.[text.Length - 1] then
                                            text.Substring(0, text.Length - 1)
                                        else
                                            text

                                    match core with
                                    | "NaN" -> Some Single.NaN
                                    | "Infinity"
                                    | "+Infinity" -> Some Single.PositiveInfinity
                                    | "-Infinity" -> Some Single.NegativeInfinity
                                    | _ ->
                                        match Single.TryParse(core, styles, invariant) with
                                        | true, value -> Some value
                                        | _ -> Some 0.0f
                                else
                                    // Pinned default DecimalFormat accepts only an
                                    // uppercase-E exponent with no positive sign.
                                    // Lowercase `1e2x` and `1E+2x` both stop at 1.
                                    let fallbackText = normalizeDecimalDigits cleaned

                                    if fallbackText.StartsWith("NaN", StringComparison.Ordinal) then
                                        Some Single.NaN
                                    elif fallbackText.StartsWith("∞", StringComparison.Ordinal) then
                                        Some Single.PositiveInfinity
                                    elif fallbackText.StartsWith("-∞", StringComparison.Ordinal) then
                                        Some Single.NegativeInfinity
                                    else
                                        let prefix =
                                            Regex.Match(
                                                fallbackText,
                                                @"^-?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:E-?[0-9]+)?"
                                            )

                                        let oversizedExponent =
                                            let exponentIndex = prefix.Value.IndexOf('E')

                                            exponentIndex >= 0
                                            && prefix.Value.Substring(exponentIndex + 1).TrimStart('-').Length > 3

                                        if String.IsNullOrEmpty prefix.Value then
                                            Some 0.0f
                                        elif oversizedExponent then
                                            // DecimalFormat's oversized-exponent
                                            // overflow/parse-stop behavior diverges
                                            // from Double.TryParse. Preserve counts
                                            // and refuse duration beyond the measured
                                            // three-digit exponent boundary.
                                            None
                                        else
                                            // DecimalFormat returns Long for an
                                            // integral value and Double otherwise.
                                            // Every integer inside the subsequent
                                            // [0, 31536000] clamp is exactly binary64;
                                            // outside it the clamp erases the width
                                            // distinction. Double->Float is therefore
                                            // the exact observable fallback path.
                                            match Double.TryParse(prefix.Value, styles, invariant) with
                                            | true, value -> Some(single value)
                                            | _ -> Some 0.0f

                            // Java Math.min/max propagate NaN, clamp infinities,
                            // and turn negative zero into positive zero here.
                            parsed
                            |> Option.map (fun value -> MathF.Max(0.0f, MathF.Min(31_536_000.0f, value)))

                        let addDuration contribution =
                            duration <-
                                match duration, contribution with
                                | Some total, Some value -> Some(total + value)
                                | _ -> None

                        let hasCaseIdentity
                            (owner: Xml.Linq.XElement)
                            (testCase: Xml.Linq.XElement)
                            =
                            // SuiteResult first uses testcase@classname, then the
                            // owning element's name. CaseResult has one final legacy
                            // fallback: a dotted testcase name supplies its class
                            // prefix. Attribute presence is the boundary; Java keeps
                            // an explicitly empty classname/name rather than treating
                            // it as missing.
                            hasAttribute "classname" testCase
                            || hasAttribute "name" owner
                            || match firstAttribute "name" testCase with
                               | None -> false
                               | Some testName -> testName.Value.Contains(".", StringComparison.Ordinal)

                        let tallyCase (element: Xml.Linq.XElement) =
                            total <- total + 1L

                            // TestResult.freeze classifies skipped first. This
                            // applies both to ordinary testcase elements and to the
                            // synthetic case constructed from a suite-level error.
                            if hasDirectChild "skipped" element then
                                skipped <- skipped + 1L
                            elif hasDirectChild "failure" element || hasDirectChild "error" element then
                                failed <- failed + 1L

                        match doc.Root with
                        | null -> ()
                        | root ->
                            // SuiteResult.parse walks direct nested <testsuite>
                            // elements. Use an explicit work stack: report nesting is
                            // untrusted input and must not consume the native call stack.
                            let pending =
                                System.Collections.Generic.Stack<Xml.Linq.XElement * bool>()

                            pending.Push(root, false)

                            while pending.Count > 0 do
                                let element, visitOwner = pending.Pop()

                                if visitOwner then
                                    // parseSuite owns direct cases/errors on every element
                                    // it reaches: the document root plus descendants reached
                                    // exclusively through direct testsuite edges. It does not
                                    // require the owner itself to be named testsuite.
                                    // A direct owner error is one synthetic case and remains
                                    // skipped when that owner also has a direct skipped marker.
                                    let directCases = directElements "testcase" element |> Seq.toArray
                                    let hasDirectError = hasDirectChild "error" element

                                    if hasDirectError then
                                        tallyCase element

                                    for testCase in directCases do
                                        let hasClassFallback =
                                            hasAttribute "classname" testCase
                                            || hasAttribute "name" element

                                        // CaseResult reads testcase@name only when both
                                        // class fallbacks are absent. A missing attribute
                                        // faults at String.contains before the later
                                        // null-className failure; name="" is present and
                                        // therefore stays on FG-211's later identity path.
                                        if
                                            not hasClassFallback
                                            && not (hasAttribute "name" testCase)
                                        then
                                            raise (MissingJUnitTestNameException())

                                        if not (hasCaseIdentity element testCase) then
                                            // CaseResult construction succeeds with a null
                                            // className. The pinned plugin does not fault until
                                            // the global tally/package pass, after every matched
                                            // report has completed construction. Remember the
                                            // deferred fault while continuing to expose any later
                                            // construction-time missing-name/read failure.
                                            sawMissingIdentity <- true

                                        tallyCase testCase

                                    // SuiteResult exists only for an owner which
                                    // contributes a direct testcase or synthetic
                                    // direct error. A present suite time is
                                    // authoritative even when it parses to zero;
                                    // otherwise add direct case times in document
                                    // order. Nested suites have already been
                                    // visited and added child-first.
                                    if hasDirectError || directCases.Length > 0 then
                                        let suiteDuration =
                                            match firstAttribute "time" element with
                                            | None ->
                                                directCases
                                                |> Array.fold (fun total testCase ->
                                                    match firstAttribute "time" testCase with
                                                    | None -> total
                                                    | Some time ->
                                                        match total, parseDuration time.Value with
                                                        | Some aggregate, Some value -> Some(aggregate + value)
                                                        | _ -> None) (Some 0.0f)
                                            | Some time -> parseDuration time.Value

                                        addDuration suiteDuration
                                else
                                    // The pinned recursive parser visits direct suite
                                    // children first, in document order, and only then
                                    // constructs the current owner. Push the continuation
                                    // first and children in reverse so this iterative walk
                                    // preserves that construction order without using
                                    // the native call stack.
                                    pending.Push(element, true)

                                    directElements "testsuite" element
                                    |> Seq.toArray
                                    |> Array.rev
                                    |> Array.iter (fun nested -> pending.Push(nested, false))
                with
                | :? System.Xml.XmlException
                    when relative.EndsWith(".xml", StringComparison.Ordinal) ->
                    // Pinned junit-plugin 1416.vd753e036de5e:
                    // TestResult.parse(File, ...) catches DocumentException for a
                    // `.xml` path and adds one failed `[failed-to-read]` case. Empty
                    // files took the earlier extension-independent `[empty]` arm.
                    total <- total + 1L
                    failed <- failed + 1L
                | :? MissingJUnitTestNameException ->
                    immediateProblem <- Some(MissingTestName relative)
                | ex ->
                    immediateProblem <-
                        Some(Unreadable($"unparsable test report(s): {relative}: {ex.GetType().Name}"))

            // Same rule as the archive path: once a report has been parsed, an
            // interruption observed afterwards still counts.
            if not aborted && abort () then aborted <- true

            match aborted, immediateProblem, sawMissingIdentity with
            | true, _, _ -> Error Interrupted
            | false, Some problem, _ -> Error problem
            | false, None, true -> Error MissingIdentity
            | false, None, false
                when [ total; failed; skipped ]
                     |> List.forall (fun value -> value >= int64 Int32.MinValue && value <= int64 Int32.MaxValue) ->
                Ok(int total, int failed, int skipped, duration)
            | false, None, false -> Error(Unreadable "test report counts exceed the JUnit Integer summary range")

    let parseJUnit (workspace: string) (patterns: string list) =
        match parseJUnitWithAbort workspace patterns (fun () -> false) with
        | Ok(total, failed, skipped, _) -> Ok(total, failed, skipped)
        | Error Interrupted -> Error "interrupted"
        | Error NoReports -> Error "no test report matched the pattern"
        | Error(MissingTestName _) -> Error JUnitDiagnostics.MissingTestNameMessage
        | Error MissingIdentity -> Error "JUnit testcase has no resolvable class identity"
        | Error(Unreadable m) -> Error m

module Stash =

    /// A stash name comes from the Jenkinsfile, which is UNTRUSTED third-party CI
    /// code. Used directly it is a path-traversal primitive, and `save` deletes its
    /// target recursively before recreating it — so `stash name: '../../..'` would
    /// have destroyed whatever it resolved to, and `unstash` would copy arbitrary
    /// controller files into the workspace. Flagged independently by both reviewers
    /// on PR #15.
    ///
    /// The name is treated as an OPAQUE KEY: a readable slug for humans plus a hash
    /// of the original, so two distinct names can never collide and no name can
    /// escape the root. The canonical result is then re-checked against the root,
    /// because a defence you have not asserted is a hope.
    let private safeKey (name: string) =
        let slug =
            String(name |> Seq.map (fun c -> if Char.IsLetterOrDigit c then c else '-') |> Seq.toArray)
            |> fun s -> s.Trim '-'
            |> fun s -> if s = "" then "stash" else s.Substring(0, min 40 s.Length)

        use sha = Security.Cryptography.SHA256.Create()
        let digest = sha.ComputeHash(Text.Encoding.UTF8.GetBytes name) |> Convert.ToHexString
        slug + "-" + digest.Substring(0, 12).ToLowerInvariant()

    let private dir (store: StashStore) (buildKey: string) (name: string) =
        let root = IO.Path.GetFullPath(IO.Path.Combine(store.Root, buildKey, "stashes"))
        let target = IO.Path.GetFullPath(IO.Path.Combine(root, safeKey name))

        // Belt and braces: assert containment rather than trusting the slug.
        if not (target.StartsWith(root + string IO.Path.DirectorySeparatorChar)) then
            failwith $"stash name '{name}' does not resolve beneath the stash root"

        target

    /// Copy the matched files out of the workspace and into controller-side storage.
    let save
        (store: StashStore)
        (buildKey: string)
        (workspace: string)
        (name: string)
        (patterns: string list)
        (excludes: string list)
        (abort: unit -> bool)
        =
        let target = dir store buildKey name
        if IO.Directory.Exists target then IO.Directory.Delete(target, true)
        IO.Directory.CreateDirectory target |> ignore

        // REVIEW FIX (Codex, PR #15 round 4): `excludes:` was parsed nowhere and applied
        // nowhere, so a stash quietly carried files the author had asked it to leave out.
        let excluded =
            excludes |> List.collect (Publish.expandGlob workspace) |> Set.ofList

        let matched =
            (if List.isEmpty patterns then [ "**" ] else patterns)
            |> List.collect (Publish.expandGlob workspace)
            |> List.distinct
            |> List.filter (fun f -> not (excluded.Contains f))
            |> List.sort

        // Same during-and-after-copy polling as the archive path: a `stash` inside a
        // `timeout` used to be able to finish AFTER the deadline with the build still
        // green, because nothing downstream observed the expiry. (FG-002e's pattern:
        // every early return is a missed poll.)
        let mutable aborted = abort ()
        let copied = System.Collections.Generic.List<string>()

        for relative in matched do
            if not aborted then
                if abort () then
                    aborted <- true
                else
                    let dest = IO.Path.Combine(target, relative)
                    IO.Directory.CreateDirectory(IO.Path.GetDirectoryName dest) |> ignore
                    IO.File.Copy(IO.Path.Combine(workspace, relative), dest, true)
                    copied.Add relative
                    if abort () then aborted <- true

        List.ofSeq copied, aborted

    /// Restore a stash into the workspace. Missing name is an error, never a silent
    /// no-op: a build that carries on with none of the files it asked for is the
    /// silent-loss shape this project exists to avoid.
    let restore
        (store: StashStore)
        (buildKey: string)
        (workspace: string)
        (name: string)
        (abort: unit -> bool)
        =
        let source = dir store buildKey name

        if not (IO.Directory.Exists source) then
            Error $"No such saved stash ‘{name}’"
        else
            let files =
                IO.Directory.GetFiles(source, "*", IO.SearchOption.AllDirectories)
                |> Array.map (fun f -> IO.Path.GetRelativePath(source, f))
                |> Array.sort

            // REVIEW FIX (Codex, PR #15): `restore` had no abort predicate and the
            // dispatcher did no post-copy check, so a large `unstash` as the final step
            // inside a `timeout` finished and reported success past the deadline.
            let mutable aborted = abort ()
            let restored = System.Collections.Generic.List<string>()

            for relative in files do
                if not aborted then
                    if abort () then
                        aborted <- true
                    else
                        let dest = IO.Path.Combine(workspace, relative)
                        IO.Directory.CreateDirectory(IO.Path.GetDirectoryName dest) |> ignore
                        IO.File.Copy(IO.Path.Combine(source, relative), dest, true)
                        restored.Add relative
                        if abort () then aborted <- true

            if aborted then Error "aborted: the step was interrupted while restoring the stash"
            else Ok(List.ofSeq restored)
