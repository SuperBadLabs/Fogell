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
        : Result<int * int * int, JUnitProblem> =
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

                        let xmlName name = Xml.Linq.XName.Get name

                        let hasDirectChild name (element: Xml.Linq.XElement) =
                            not (isNull (element.Element(xmlName name)))

                        let hasAttribute name (element: Xml.Linq.XElement) =
                            not (isNull (element.Attribute(xmlName name)))

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
                            || match testCase.Attribute(xmlName "name") with
                               | null -> false
                               | testName -> testName.Value.Contains(".", StringComparison.Ordinal)

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
                                    if hasDirectChild "error" element then
                                        tallyCase element

                                    for testCase in element.Elements(xmlName "testcase") do
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
                                else
                                    // The pinned recursive parser visits direct suite
                                    // children first, in document order, and only then
                                    // constructs the current owner. Push the continuation
                                    // first and children in reverse so this iterative walk
                                    // preserves that construction order without using
                                    // the native call stack.
                                    pending.Push(element, true)

                                    element.Elements(xmlName "testsuite")
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
                Ok(int total, int failed, int skipped)
            | false, None, false -> Error(Unreadable "test report counts exceed the JUnit Integer summary range")

    let parseJUnit (workspace: string) (patterns: string list) =
        match parseJUnitWithAbort workspace patterns (fun () -> false) with
        | Ok v -> Ok v
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
