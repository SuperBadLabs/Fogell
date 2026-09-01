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

    type private OldJUnitReportException() =
        inherit IOException("JUnit report predates the build timestamp boundary")

    type private EmptyJUnitReportException() =
        inherit IOException("JUnit report is empty")

    let private compileGlobRegex (caseSensitive: bool) (pattern: string) =
        let normalised = pattern.Replace('\\', '/').Trim()

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

        let options =
            if caseSensitive then RegexOptions.None else RegexOptions.IgnoreCase

        Regex("^" + escaped + "$", options)

    // Ant 1.10.17 DirectoryScanner.DEFAULTEXCLUDES. JUnit creates a FileSet and
    // leaves useDefaultExcludes at its true default; stash does the same unless
    // its public flag is explicitly false. These exclusions therefore remain
    // active even when an include names one of the paths literally. The pinned
    // list is case-sensitive and shared only by those two Ant-backed selectors;
    // archive retains its established matcher and behavior.
    let private antDefaultExcludePatterns =
        [ "**/*~"
          "**/#*#"
          "**/.#*"
          "**/%*%"
          "**/._*"
          "**/CVS"
          "**/CVS/**"
          "**/.cvsignore"
          "**/SCCS"
          "**/SCCS/**"
          "**/vssver.scc"
          "**/.svn"
          "**/.svn/**"
          "**/.git"
          "**/.git/**"
          "**/.gitattributes"
          "**/.gitignore"
          "**/.gitmodules"
          "**/.hg"
          "**/.hg/**"
          "**/.hgignore"
          "**/.hgsub"
          "**/.hgsubstate"
          "**/.hgtags"
          "**/.bzr"
          "**/.bzr/**"
          "**/.bzrignore"
          "**/.DS_Store" ]

    let private antDefaultExcludeRegexes =
        antDefaultExcludePatterns |> List.map (compileGlobRegex true)

    let internal isAntDefaultExcluded (relative: string) =
        antDefaultExcludeRegexes |> List.exists (fun regex -> regex.IsMatch relative)

    let internal matchesGlob (caseSensitive: bool) (pattern: string) (relative: string) =
        compileGlobRegex caseSensitive pattern |> fun regex -> regex.IsMatch relative

    /// Expand a Jenkins-style ant glob (`**/*.jar`, `target/*.txt`, `out.txt`)
    /// against a workspace. Deliberately supports only the forms measured in the
    /// corpus; anything else is reported rather than silently matching nothing.
    let private expandGlobWithCase (caseSensitive: bool) (workspace: string) (pattern: string) : string list =
        if not (Directory.Exists workspace) then
            []
        else
            let regex = compileGlobRegex caseSensitive pattern

            Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
            |> Array.choose (fun full ->
                let relative = Path.GetRelativePath(workspace, full).Replace('\\', '/')
                if regex.IsMatch relative then Some relative else None)
            |> Array.sort
            |> Array.toList

    // Existing archive/stash compatibility is case-insensitive. JUnit's pinned
    // Ant FileSet keeps DirectoryScanner.caseSensitive at its true default, so
    // report selection uses a separate exact-case entry point.
    let expandGlob workspace pattern = expandGlobWithCase false workspace pattern

    // Ant tokenizes include patterns by path component and discards empty tokens,
    // so repeated separators inside a JUnit include are equivalent to one. It also
    // appends `**` when an include ends in a separator. For wildcard-bearing
    // prefixes, the suffix-free arm below represents terminal `**` consuming zero
    // components (for example `reports/*/` also selects `reports/result.xml`);
    // the suffixed arm selects descendants. A wholly literal prefix stays a
    // directory lookup: Ant does not reinterpret a literal `report.xml/` as the
    // file `report.xml`.
    // Keep both rules private to JUnit: archive/stash retain their shared matcher.
    // Collapsing instead of removing separators preserves rooted patterns as rooted.
    let private normalizeJUnitPatterns (pattern: string) =
        let normalized =
            pattern.Replace('\\', '/')
            |> fun value -> Regex.Replace(value, "/+", "/")

        if normalized.EndsWith("/", StringComparison.Ordinal) then
            let prefix = normalized.Substring(0, normalized.Length - 1)

            if prefix.Contains('*') || prefix.Contains('?') then
                [ prefix; normalized + "**" ]
            else
                [ normalized + "**" ]
        else
            [ normalized ]

    type private JUnitPatternSpec =
        { Matcher: Regex
          IsLiteral: bool
          LiteralPrefix: string
          IsRooted: bool }

    type private JUnitFileCandidate =
        { Relative: string
          PhysicalPath: string }

    type private JUnitSelectedCandidate =
        { Relative: string
          PhysicalTarget: string option }

    let private compileJUnitPatternSpec (normalized: string) =
        let prefix =
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            |> Array.takeWhile (fun segment -> not (segment.Contains('*') || segment.Contains('?')))
            |> String.concat "/"

        { Matcher = compileGlobRegex true normalized
          IsLiteral = not (normalized.Contains('*') || normalized.Contains('?'))
          LiteralPrefix = prefix
          IsRooted = Path.IsPathRooted normalized }

    // This is deliberately conservative after the first wildcard: it may walk
    // extra descendants under the literal prefix, but never an unrelated top-
    // level tree for a narrow include. The shared work budget bounds that extra.
    let private junitPatternMayMatchDescendant (spec: JUnitPatternSpec) logicalDirectory =
        if spec.IsRooted then
            false
        elif String.IsNullOrEmpty spec.LiteralPrefix || String.IsNullOrEmpty logicalDirectory then
            true
        else
            spec.LiteralPrefix = logicalDirectory
            || spec.LiteralPrefix.StartsWith(logicalDirectory + "/", StringComparison.Ordinal)
            || logicalDirectory.StartsWith(spec.LiteralPrefix + "/", StringComparison.Ordinal)

    let private junitScanLimit = 100_000

    // Ant DirectoryScanner follows healthy links by their logical scanner path,
    // including directory links whose target is outside the FileSet base. Its
    // cycle guard is not a global symlink-depth limit: it prunes a branch only
    // after the same canonical directory target has been followed five times.
    // Enumerate JUnit inputs privately so archive/stash keep their established
    // matcher. The explicit stack, cancellation polling, pattern pruning, and a
    // hard logical-entry ceiling bound CPU, memory, and queue growth.
    let private enumerateJUnitFiles
        (scanLimit: int)
        (workspace: string)
        (specs: JUnitPatternSpec list)
        (workUnits: int ref)
        (abort: unit -> bool)
        : Result<JUnitFileCandidate list, JUnitProblem> =
        if not (Directory.Exists workspace) then
            Ok []
        else
            let files = System.Collections.Generic.List<JUnitFileCandidate>()
            let pending =
                System.Collections.Generic.Stack<string * string * Map<string, int> * bool>()
            let mutable problem: JUnitProblem option = None

            let chargeWork () =
                if workUnits.Value >= scanLimit then
                    problem <-
                        Some(
                            Unreadable(
                                $"JUnit report scan exceeded the {scanLimit} logical-entry safety limit"))
                    false
                else
                    workUnits.Value <- workUnits.Value + 1
                    true

            let anySpec (predicate: JUnitPatternSpec -> bool) =
                let mutable matched = false
                use enumerator = (specs :> seq<JUnitPatternSpec>).GetEnumerator()

                while Option.isNone problem && not matched && enumerator.MoveNext() do
                    if chargeWork () then
                        matched <- predicate enumerator.Current

                matched

            pending.Push(workspace, "", Map.empty, false)

            try
                while pending.Count > 0 && Option.isNone problem do
                    if abort () then
                        problem <- Some Interrupted
                    else
                        let physicalDirectory, logicalDirectory, followedTargets, missingAllowed = pending.Pop()

                        try
                            use entries = DirectoryInfo(physicalDirectory).EnumerateFileSystemInfos().GetEnumerator()

                            while Option.isNone problem && entries.MoveNext() do
                                if abort () then
                                    problem <- Some Interrupted
                                elif chargeWork () then
                                    let entry = entries.Current
                                    let logical =
                                        if logicalDirectory = "" then entry.Name
                                        else logicalDirectory + "/" + entry.Name

                                    let excluded = isAntDefaultExcluded logical
                                    let selectFile =
                                        not excluded && anySpec (fun spec -> spec.Matcher.IsMatch logical)
                                    let descend =
                                        not excluded
                                        && anySpec (fun spec -> junitPatternMayMatchDescendant spec logical)

                                    // Do not even resolve a symlink whose logical
                                    // path cannot contribute to an include.
                                    if selectFile || descend then
                                        let isLink = not (isNull entry.LinkTarget)

                                        if isLink then
                                            try
                                                let directTarget =
                                                    if Path.IsPathRooted entry.LinkTarget then entry.LinkTarget
                                                    else Path.Combine(Path.GetDirectoryName entry.FullName, entry.LinkTarget)

                                                let isDirectSelfLoop =
                                                    String.Equals(
                                                        Path.GetFullPath directTarget,
                                                        Path.GetFullPath entry.FullName,
                                                        StringComparison.Ordinal)

                                                if isDirectSelfLoop then
                                                    if selectFile then
                                                        files.Add
                                                            { Relative = logical
                                                              PhysicalPath = entry.FullName }
                                                else
                                                    let target = entry.ResolveLinkTarget(true)

                                                    if not (isNull target) then
                                                        if
                                                            target :? DirectoryInfo
                                                            || (descend && not selectFile && not target.Exists)
                                                        then
                                                            if descend then
                                                                let canonicalTarget = Path.GetFullPath target.FullName
                                                                let followed =
                                                                    followedTargets
                                                                    |> Map.tryFind canonicalTarget
                                                                    |> Option.defaultValue 0

                                                                if followed < 5 then
                                                                    pending.Push(
                                                                        target.FullName,
                                                                        logical,
                                                                        followedTargets
                                                                        |> Map.add canonicalTarget (followed + 1),
                                                                        true)
                                                        elif selectFile then
                                                            files.Add
                                                                { Relative = logical
                                                                  PhysicalPath = entry.FullName }
                                            with
                                            | :? FileNotFoundException
                                            | :? DirectoryNotFoundException ->
                                                // A wildcard-selected file link
                                                // that vanishes during resolution
                                                // remains a lexical synthetic
                                                // candidate. Literal selection is
                                                // filtered by its later open probe.
                                                if selectFile then
                                                    files.Add
                                                        { Relative = logical
                                                          PhysicalPath = entry.FullName }
                                            | ex ->
                                                problem <-
                                                    Some(
                                                        Unreadable(
                                                            $"JUnit report scan failed: {logical}: {ex.GetType().Name}"))
                                        elif entry :? DirectoryInfo then
                                            if descend then
                                                pending.Push(entry.FullName, logical, followedTargets, false)
                                        elif selectFile then
                                            // Wildcard scans retain dangling file
                                            // links lexically; the literal fast
                                            // path filters them after resolution.
                                            files.Add
                                                { Relative = logical
                                                  PhysicalPath = entry.FullName }
                        with
                        | (:? FileNotFoundException | :? DirectoryNotFoundException)
                            when missingAllowed -> ()
                        | ex ->
                            problem <-
                                Some(
                                    Unreadable(
                                        $"JUnit report scan failed: {logicalDirectory}: {ex.GetType().Name}"))

                match problem with
                | Some failure -> Error failure
                | None -> Ok(files |> Seq.sort |> Seq.toList)
            with ex ->
                match problem with
                | Some Interrupted -> Error Interrupted
                | Some failure -> Error failure
                | None -> Error(Unreadable($"JUnit report scan failed: {ex.GetType().Name}"))

    // FileInfo.Exists/Length do not consistently follow a dangling Unix link the
    // way java.io.File does: Exists may describe the link entry and Length can
    // then throw while Java's exists()/length() report false/zero. Resolve links
    // explicitly for the JUnit paths whose Ant/Jenkins behavior depends on the
    // final target. Other publishers intentionally keep their existing semantics.
    type private JUnitTargetResolution =
        | TargetMissing
        | TargetFound of FileInfo
        | TargetUnreadable of exn

    let private junitFileTargetInfo (fullPath: string) =
        let candidate = FileInfo fullPath

        try
            let linkTarget = candidate.LinkTarget

            if isNull linkTarget then
                candidate.Refresh()
                if candidate.Exists then TargetFound candidate else TargetMissing
            else
                let directTarget =
                    if Path.IsPathRooted linkTarget then linkTarget
                    else Path.Combine(candidate.DirectoryName, linkTarget)

                // The measured file self-loop is absence in java.io.File terms.
                // Recognise only that exact loop; other resolver/I/O failures
                // must remain distinguishable from a dangling target.
                if
                    String.Equals(
                        Path.GetFullPath directTarget,
                        Path.GetFullPath candidate.FullName,
                        StringComparison.Ordinal)
                then
                    TargetMissing
                else
                    match candidate.ResolveLinkTarget(true) with
                    | null -> TargetMissing
                    | target ->
                        let targetInfo = FileInfo target.FullName
                        targetInfo.Refresh()
                        // ResolveLinkTarget returns a physical path even when its
                        // final target is absent. Preserve that path so an open can
                        // distinguish FileNotFound from authority and other I/O.
                        TargetFound targetInfo
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> TargetMissing
        | ex -> TargetUnreadable ex

    let private expandJUnitGlobs
        (scanLimit: int)
        (workspace: string)
        (patterns: string list)
        (abort: unit -> bool) =
        let rawPatternWork =
            patterns
            |> List.fold (fun total pattern ->
                min (scanLimit + 1) (total + pattern.Length + 1)) 0

        let patternLimitExceeded = rawPatternWork > scanLimit
        let normalized =
            if patternLimitExceeded then []
            else patterns |> List.collect normalizeJUnitPatterns
        let specs = normalized |> List.map compileJUnitPatternSpec
        let workUnits = ref rawPatternWork
        let scanResult = enumerateJUnitFiles scanLimit workspace specs workUnits abort

        match patternLimitExceeded, scanResult with
        | true, _ ->
            Error(
                Unreadable(
                    $"JUnit report scan exceeded the {scanLimit} logical-entry safety limit"))
        | false, Error problem -> Error problem
        | false, Ok files ->
            let selected = System.Collections.Generic.List<JUnitSelectedCandidate>()
            let mutable problem: JUnitProblem option = None
            use patternEnumerator = (specs :> seq<JUnitPatternSpec>).GetEnumerator()

            while Option.isNone problem && patternEnumerator.MoveNext() do
                let spec = patternEnumerator.Current
                use candidates = (files :> seq<JUnitFileCandidate>).GetEnumerator()

                while Option.isNone problem && candidates.MoveNext() do
                  if abort () then
                    problem <- Some Interrupted
                  elif workUnits.Value >= scanLimit then
                    problem <-
                        Some(
                            Unreadable(
                                $"JUnit report scan exceeded the {scanLimit} logical-entry safety limit"))
                  else
                    workUnits.Value <- workUnits.Value + 1
                    let candidate = candidates.Current

                    if spec.Matcher.IsMatch candidate.Relative then
                      // Resolve a final file link exactly once during selection.
                      // The stored physical target survives a later retarget of
                      // either the file link or an ancestor directory link.
                      match junitFileTargetInfo candidate.PhysicalPath with
                        | TargetFound targetInfo ->
                            if spec.IsLiteral then
                                try
                                    use probe =
                                        new FileStream(
                                            targetInfo.FullName,
                                            FileMode.Open,
                                            FileAccess.Read,
                                            FileShare.ReadWrite ||| FileShare.Delete)
                                    selected.Add
                                        { Relative = candidate.Relative
                                          PhysicalTarget = Some targetInfo.FullName }
                                with
                                | :? FileNotFoundException
                                | :? DirectoryNotFoundException -> ()
                                | ex ->
                                    problem <-
                                        Some(
                                            Unreadable(
                                                $"unparsable test report(s): {candidate.Relative}: {ex.GetType().Name}"))
                            else
                                selected.Add
                                    { Relative = candidate.Relative
                                      PhysicalTarget = Some targetInfo.FullName }
                        | TargetMissing ->
                            if not spec.IsLiteral then
                                selected.Add
                                    { Relative = candidate.Relative
                                      PhysicalTarget = None }
                        | TargetUnreadable ex ->
                            problem <-
                                Some(
                                    Unreadable(
                                        $"unparsable test report(s): {candidate.Relative}: {ex.GetType().Name}"))

            match problem with
            | Some failure -> Error failure
            | None ->
                Ok(
                    selected
                    |> Seq.distinctBy (fun candidate -> candidate.Relative)
                    |> Seq.sortBy (fun candidate -> candidate.Relative)
                    |> Seq.toList)

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
    let internal parseJUnitWithAbortUsingScanLimit
        (scanLimit: int)
        (workspace: string)
        (patterns: string list)
        (skipOldReportsSince: int64 option)
        (abort: unit -> bool)
        : Result<int * int * int * single option, JUnitProblem> =
        let files, selectionProblem =
            match expandJUnitGlobs scanLimit workspace patterns abort with
            | Ok selected -> selected, None
            | Error problem -> [], Some problem

        // REVIEW FIX (Codex, PR #14 round 12): the mirror of the archive zero-match
        // case. With no matching report the scan can still have been long, and an
        // interrupt firing during it was reported as "no test report matched" — a
        // pattern problem the user would go and debug — instead of an abort.
        let scanInterrupted =
            match selectionProblem with
            | Some Interrupted -> true
            | _ -> false

        if scanInterrupted then
            Error Interrupted
        elif abort () then
            Error Interrupted
        elif Option.isSome selectionProblem then
            Error selectionProblem.Value
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

            for candidate in files do
              if not aborted && Option.isNone immediateProblem then
                let relative = candidate.Relative

                if abort () then
                    aborted <- true
                else
                try
                    // Jenkins calls File.length() before opening or parsing. Java
                    // returns zero both for a zero-byte file and for a path which
                    // vanished after glob expansion, so both become one synthetic
                    // `[empty]` failure without an open attempt. A non-empty parse
                    // failure is recovered only for an exact lowercase `.xml` path.
                    match candidate.PhysicalTarget with
                    | None ->
                        match skipOldReportsSince with
                        | Some _ ->
                            immediateProblem <-
                                Some(
                                    Unreadable(
                                        $"unparsable test report(s): {relative}: FileNotFoundException"))
                        | None ->
                            total <- total + 1L
                            failed <- failed + 1L
                    | Some physicalTarget ->
                        // Resolve once, open once, and use this handle for length,
                        // timestamp, and XML bytes. Retargeting the logical link
                        // cannot split metadata from parsed content. `/proc/self/fd`
                        // names the open inode on the pinned Linux agent.
                        use stream =
                            new FileStream(
                                physicalTarget,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite ||| FileShare.Delete)

                        match skipOldReportsSince with
                        | Some buildStartTimeInMillis ->
                            let modified =
                                File.GetLastWriteTimeUtc(stream.SafeFileHandle)
                                |> DateTimeOffset
                                |> fun value -> value.ToUnixTimeMilliseconds()

                            // Pinned JUnit tolerates timestamp precision by 3000
                            // ms and retains equality at the adjusted boundary.
                            if modified < buildStartTimeInMillis - 3000L then
                                raise (OldJUnitReportException())
                        | None -> ()

                        if stream.Length = 0L then
                            raise (EmptyJUnitReportException())

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
                | :? OldJUnitReportException -> ()
                | :? EmptyJUnitReportException ->
                    total <- total + 1L
                    failed <- failed + 1L
                | (:? FileNotFoundException | :? DirectoryNotFoundException)
                    when Option.isNone skipOldReportsSince ->
                    // A wildcard-selected dangling target is one synthetic empty
                    // report. Timestamp filtering deliberately keeps the existing
                    // unreadable FileNotFound classification instead.
                    total <- total + 1L
                    failed <- failed + 1L
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

    let parseJUnitWithAbort workspace patterns skipOldReportsSince abort =
        parseJUnitWithAbortUsingScanLimit
            junitScanLimit
            workspace
            patterns
            skipOldReportsSince
            abort

    let parseJUnit (workspace: string) (patterns: string list) =
        match parseJUnitWithAbort workspace patterns None (fun () -> false) with
        | Ok(total, failed, skipped, _) -> Ok(total, failed, skipped)
        | Error Interrupted -> Error "interrupted"
        | Error NoReports -> Error "no test report matched the pattern"
        | Error(MissingTestName _) -> Error JUnitDiagnostics.MissingTestNameMessage
        | Error MissingIdentity -> Error "JUnit testcase has no resolvable class identity"
        | Error(Unreadable m) -> Error m

module Stash =

    let private safeDiagnosticPath (value: string) =
        let builder = Text.StringBuilder()
        let limit = min value.Length 160

        for index in 0 .. limit - 1 do
            let c = value.[index]
            if Char.IsControl c then builder.Append($"\\u{int c:X4}") |> ignore
            else builder.Append c |> ignore

        if value.Length > limit then builder.Append "…" |> ignore
        builder.ToString()

    [<RequireQualifiedAccess>]
    type SaveProblem =
        | SelectedPathRefused of relative: string * detail: string
        | StorageFailure of detail: string

        member this.Describe =
            match this with
            | SelectedPathRefused(relative, detail) ->
                $"stash refuses selected path ‘{safeDiagnosticPath relative}’: {detail}"
            | StorageFailure detail -> $"stash storage failed: {detail}"

    let private segmentMatches (pattern: string) (value: string) =
        let expression =
            pattern
            |> Regex.Escape
            |> fun escaped -> escaped.Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]")

        Regex.IsMatch(value, "^" + expression + "$", RegexOptions.IgnoreCase)

    /// Whether an include can select this directory or something beneath it.
    /// This is a prefix question, not a filesystem walk: a selected directory
    /// link is refused without enumerating even one target entry. `**` may
    /// consume zero or more components; every remaining ordinary component can
    /// be satisfied by some descendant name once the known prefix is consumed.
    let private patternMaySelectDirectory (pattern: string) (relative: string) =
        let normalized = pattern.Replace('\\', '/').Trim()

        if Path.IsPathRooted normalized then
            false
        else
            let patternSegments = normalized.Split('/', StringSplitOptions.None)
            let relativeSegments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
            let memo = Collections.Generic.Dictionary<int * int, bool>()

            let rec matches patternIndex relativeIndex =
                match memo.TryGetValue((patternIndex, relativeIndex)) with
                | true, value -> value
                | false, _ ->
                    let value =
                        if relativeIndex = relativeSegments.Length then
                            // The known directory prefix is compatible. Any
                            // remaining literal/wildcard pattern components are
                            // a question about target contents, which Fogell does
                            // not inspect through a link.
                            patternIndex < patternSegments.Length
                            && patternSegments.[patternIndex..]
                               |> Array.forall (fun segment -> segment <> "")
                        elif patternIndex = patternSegments.Length then
                            false
                        elif patternSegments.[patternIndex] = "**" then
                            matches (patternIndex + 1) relativeIndex
                            || matches patternIndex (relativeIndex + 1)
                        elif segmentMatches patternSegments.[patternIndex] relativeSegments.[relativeIndex] then
                            matches (patternIndex + 1) (relativeIndex + 1)
                        else
                            false

                    memo.[(patternIndex, relativeIndex)] <- value
                    value

            matches 0 0

    /// Enumerate only physical workspace directories. Link targets are never
    /// entered: selected file links and selected directory prefixes become a
    /// named refusal at their lexical path, while unselected links are ignored.
    let private entryIsSymbolicLink (entry: FileSystemInfo) =
        not (isNull entry.LinkTarget)

    let private selectWithoutFollowingLinks
        (workspace: string)
        (patterns: string list)
        (excludes: string list)
        (useDefaultExcludes: bool)
        (abort: unit -> bool)
        =
        let rawIncludes = if List.isEmpty patterns then [ "**" ] else patterns
        let normalizePattern (pattern: string) = pattern.Replace('\\', '/').Trim()
        let includes =
            rawIncludes
            |> List.filter (fun includePattern ->
                excludes
                |> List.exists (fun excludePattern ->
                    String.Equals(
                        normalizePattern includePattern,
                        normalizePattern excludePattern,
                        StringComparison.OrdinalIgnoreCase))
                |> not)
        let files = Collections.Generic.List<string>()
        let pending = Collections.Generic.Stack<Microsoft.Win32.SafeHandles.SafeFileHandle * string>()
        let mutable problem: SaveProblem option = None
        let mutable aborted = abort ()

        match Native.openDirectoryWithoutLinks workspace with
        | Ok root -> pending.Push(root, "")
        | Error why -> problem <- Some(SaveProblem.StorageFailure why)

        let explicitlyExcluded relative =
            excludes |> List.exists (fun pattern -> Publish.matchesGlob false pattern relative)

        let explicitlyExcludesDirectory relative =
            excludes
            |> List.exists (fun pattern ->
                let normalized = pattern.Replace('\\', '/').Trim()
                normalized.EndsWith("/**", StringComparison.Ordinal)
                && Publish.matchesGlob false (normalized.Substring(0, normalized.Length - 3)) relative)

        let defaultExcluded relative =
            useDefaultExcludes && Publish.isAntDefaultExcluded relative

        try
            while pending.Count > 0 && Option.isNone problem && not aborted do
                let descriptor, logicalDirectory = pending.Pop()
                use descriptor = descriptor

                use entries =
                    DirectoryInfo(Native.directoryDescriptorPath descriptor)
                        .EnumerateFileSystemInfos()
                        .GetEnumerator()

                while entries.MoveNext() && Option.isNone problem && not aborted do
                    if abort () then
                        aborted <- true
                    else
                        let entry = entries.Current
                        let relative =
                            if logicalDirectory = "" then entry.Name
                            else logicalDirectory + "/" + entry.Name

                        let isLink = entryIsSymbolicLink entry
                        let isDirectory = entry :? DirectoryInfo
                        let selected =
                            if isDirectory then
                                includes |> List.exists (fun pattern -> patternMaySelectDirectory pattern relative)
                            else
                                includes |> List.exists (fun pattern -> Publish.matchesGlob false pattern relative)

                        let excluded =
                            explicitlyExcluded relative
                            || defaultExcluded relative
                            || (isDirectory && explicitlyExcludesDirectory relative)

                        if isLink then
                            if selected && not excluded then
                                problem <-
                                    Some(
                                        SaveProblem.SelectedPathRefused(
                                            relative,
                                            "selected symbolic links and linked directory descendants are not stashed"))
                        elif isDirectory then
                            if not excluded then
                                match Native.openChildDirectoryWithoutLinks descriptor entry.Name with
                                | Ok child -> pending.Push(child, relative)
                                | Error why ->
                                    problem <-
                                        Some(
                                            SaveProblem.SelectedPathRefused(
                                                relative,
                                                why))
                        elif selected && not excluded then
                            files.Add relative
        with ex ->
            problem <-
                Some(
                    SaveProblem.StorageFailure(
                        $"could not enumerate stash inputs ({ex.GetType().Name})"))

        while pending.Count > 0 do
            let descriptor, _ = pending.Pop()
            descriptor.Dispose()

        problem, (files |> Seq.distinct |> Seq.sort |> Seq.toList), aborted

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
        (useDefaultExcludes: bool)
        (abort: unit -> bool)
        : Result<string list * bool, SaveProblem> =
        let target = dir store buildKey name

        let selectionProblem, matched, selectionAborted =
            selectWithoutFollowingLinks workspace patterns excludes useDefaultExcludes abort

        // Same during-and-after-copy polling as the archive path: a `stash` inside a
        // `timeout` used to be able to finish AFTER the deadline with the build still
        // green, because nothing downstream observed the expiry. (FG-002e's pattern:
        // every early return is a missed poll.)
        let mutable aborted = selectionAborted || abort ()
        let copied = System.Collections.Generic.List<string>()
        let mutable problem = selectionProblem
        let staging = target + ".new-" + Guid.NewGuid().ToString "N"

        IO.Directory.CreateDirectory staging |> ignore

        for relative in matched do
            if not aborted && Option.isNone problem then
                if abort () then
                    aborted <- true
                else
                    match Native.openFileWithoutLinks workspace relative with
                    | Error why ->
                        problem <- Some(SaveProblem.SelectedPathRefused(relative, why))
                    | Ok source ->
                        use source = source
                        try
                            let dest = IO.Path.Combine(staging, relative)
                            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName dest) |> ignore
                            use output =
                                new FileStream(
                                    dest,
                                    FileMode.CreateNew,
                                    FileAccess.Write,
                                    FileShare.None)
                            source.CopyTo output
                            copied.Add relative
                            if abort () then aborted <- true
                        with ex ->
                            problem <-
                                Some(
                                    SaveProblem.StorageFailure(
                                        $"could not stage ‘{relative}’ ({ex.GetType().Name})"))

        match problem, aborted with
        | Some refusal, _ ->
            IO.Directory.Delete(staging, true)
            Error refusal
        | None, true ->
            IO.Directory.Delete(staging, true)
            Ok(List.ofSeq copied, true)
        | None, false ->
            // Publish only a completely validated copy. In particular, a refused
            // same-name replacement leaves the prior stash intact rather than
            // replacing it with a partial archive assembled before the bad link.
            let backup = target + ".old-" + Guid.NewGuid().ToString "N"

            try
                if IO.Directory.Exists target then IO.Directory.Move(target, backup)

                try
                    IO.Directory.Move(staging, target)
                with _ ->
                    if IO.Directory.Exists backup && not (IO.Directory.Exists target) then
                        IO.Directory.Move(backup, target)
                    reraise()

                if IO.Directory.Exists backup then
                    try
                        IO.Directory.Delete(backup, true)
                    with _ ->
                        // The target move above is the commit point. Cleanup of
                        // the now-obsolete prior tree cannot turn a committed
                        // stash into a reported failure; a later maintenance
                        // sweep may remove an orphaned .old-* directory.
                        ()
                Ok(List.ofSeq copied, false)
            with ex ->
                if IO.Directory.Exists staging then IO.Directory.Delete(staging, true)
                Error(SaveProblem.StorageFailure($"could not publish staged stash ({ex.GetType().Name})"))

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
            let selectionProblem, files, selectionAborted =
                selectWithoutFollowingLinks source [ "**" ] [] false abort

            // REVIEW FIX (Codex, PR #15): `restore` had no abort predicate and the
            // dispatcher did no post-copy check, so a large `unstash` as the final step
            // inside a `timeout` finished and reported success past the deadline.
            let mutable aborted = selectionAborted || abort ()
            let mutable problem =
                selectionProblem
                |> Option.map (function
                    | SaveProblem.SelectedPathRefused(relative, _) ->
                        $"unstash refuses stored path ‘{safeDiagnosticPath relative}’: stored symbolic links and linked directory descendants are not restored"
                    | SaveProblem.StorageFailure detail ->
                        $"unstash storage read failed: {detail}")
            let restored = System.Collections.Generic.List<string>()

            for relative in files do
                if not aborted && Option.isNone problem then
                    if abort () then
                        aborted <- true
                    else
                        match Native.openFileWithoutLinks source relative with
                        | Error why ->
                            problem <- Some $"unstash refuses stored path ‘{relative}’: {why}"
                        | Ok input ->
                            use input = input

                            match Native.createWorkspaceFileWithoutLinks workspace relative with
                            | Error why ->
                                problem <- Some $"unstash refuses restore path ‘{relative}’: {why}"
                            | Ok output ->
                                use output = output
                                try
                                    input.CopyTo output
                                    restored.Add relative
                                    if abort () then aborted <- true
                                with ex ->
                                    problem <- Some $"unstash could not restore ‘{relative}’ ({ex.GetType().Name})"

            match problem, aborted with
            | Some why, _ -> Error why
            | None, true -> Error "aborted: the step was interrupted while restoring the stash"
            | None, false -> Ok(List.ofSeq restored)
