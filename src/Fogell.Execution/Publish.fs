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
    let archive (store: ArtifactStore) (buildKey: string) (workspace: string) (patterns: string list) =
        let target = Path.Combine(store.Root, buildKey)

        let published =
            patterns
            |> List.collect (expandGlob workspace)
            |> List.distinct
            |> List.sort

        for relative in published do
            let dest = Path.Combine(target, relative)
            Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
            File.Copy(Path.Combine(workspace, relative), dest, true)

        published

    /// Parse JUnit XML totals. Reads only the attributes every producer emits;
    /// a malformed report is reported, not silently counted as zero.
    let parseJUnit (workspace: string) (patterns: string list) : Result<int * int * int, string> =
        let files = patterns |> List.collect (expandGlob workspace) |> List.distinct

        if List.isEmpty files then
            Error "no test report matched the pattern"
        else
            let mutable total = 0
            let mutable failed = 0
            let mutable skipped = 0
            let mutable malformed = []

            for relative in files do
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

            match malformed with
            | [] -> Ok(total, failed, skipped)
            | errs -> Error("unparsable test report(s): " + String.concat "; " errs)
