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
    | Unreadable of string

module Publish =

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

    /// Parse JUnit XML totals. Reads only the attributes every producer emits;
    /// a malformed report is reported, not silently counted as zero.
    /// `abort` is polled between report files. REVIEW FIX (Codex, PR #14 round 9):
    /// StepRequest.DeadlineExpired was documented as polled by "archive, junit" and
    /// only archive read it, so a `timeout` whose last step is `junit` could scan many
    /// reports and return Success or Unstable after the deadline.
    let parseJUnitWithAbort
        (workspace: string)
        (patterns: string list)
        (abort: unit -> bool)
        : Result<int * int * int, JUnitProblem> =
        let files = patterns |> List.collect (expandGlob workspace) |> List.distinct

        if List.isEmpty files then
            Error(Unreadable "no test report matched the pattern")
        else
            let mutable total = 0
            let mutable failed = 0
            let mutable skipped = 0
            let mutable malformed = []

            let mutable aborted = false

            for relative in files do
              if not aborted then
                if abort () then
                    aborted <- true
                else
                try
                    let doc = Xml.Linq.XDocument.Load(Path.Combine(workspace, relative))

                    // Sum over <testsuite> elements; a <testsuites> wrapper is
                    // common, and counting both would double every figure.
                    let suites =
                        doc.Descendants(Xml.Linq.XName.Get "testsuite") |> Seq.toList

                    let readInt (e: Xml.Linq.XElement) name =
                        match e.Attribute(Xml.Linq.XName.Get name) with
                        | null -> 0
                        | a ->
                            match Int32.TryParse a.Value with
                            | true, v -> v
                            | _ -> 0

                    for suite in suites do
                        total <- total + readInt suite "tests"
                        failed <- failed + readInt suite "failures" + readInt suite "errors"
                        skipped <- skipped + readInt suite "skipped"
                with ex ->
                    malformed <- $"{relative}: {ex.GetType().Name}" :: malformed

            // Same rule as the archive path: once a report has been parsed, an
            // interruption observed afterwards still counts.
            if not aborted && abort () then aborted <- true

            match aborted, malformed with
            | true, _ -> Error Interrupted
            | false, [] -> Ok(total, failed, skipped)
            | false, errs -> Error(Unreadable("unparsable test report(s): " + String.concat "; " errs))

    let parseJUnit (workspace: string) (patterns: string list) =
        match parseJUnitWithAbort workspace patterns (fun () -> false) with
        | Ok v -> Ok v
        | Error Interrupted -> Error "interrupted"
        | Error(Unreadable m) -> Error m
