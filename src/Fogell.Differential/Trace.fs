namespace Fogell.Differential

open System
open System.IO
open System.Security.Cryptography

/// FG-002. The canonical form both engines are reduced to before comparison.
///
/// The hard part of a differential harness is deciding what "the same" means.
/// Comparing raw console output is hopeless — timestamps, node names, plugin
/// banners and ANSI codes all differ and none of it is semantics. So a run is
/// reduced to three things that *are* semantics:
///
///   1. the terminal result
///   2. the ordered sequence of observable step outputs
///   3. a canonical hash of the workspace the run produced
///
/// Anything outside those three is deliberately not compared, and the reasons
/// are recorded here rather than left implicit.
type Trace =
    { /// success | failure | aborted | unstable
      Result: string
      /// Ordered, normalised output lines attributable to the pipeline itself.
      Output: string list
      /// SHA-256 over (relative path, content hash) pairs, sorted.
      WorkspaceHash: string
      /// Files in the workspace, for a readable diff when hashes disagree.
      WorkspaceFiles: (string * string) list }

module Trace =

    let private sha256Hex (bytes: byte[]) =
        use h = SHA256.Create()
        h.ComputeHash bytes |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()

    let private sha256Text (text: string) = sha256Hex (Text.Encoding.UTF8.GetBytes text)

    /// Paths that are execution scaffolding rather than build output. Excluded
    /// from the workspace hash because their presence is an engine detail:
    /// Jenkins writes an `@tmp` sibling and durable-task spool files; Fogell
    /// writes nothing comparable.
    let private isScaffolding (relative: string) =
        let p = relative.Replace('\\', '/')

        p.StartsWith ".git/"
        || p.Contains "@tmp/"
        || p.EndsWith ".pid"
        || p.StartsWith "durable-"
        || p.Contains "/durable-"
        || Path.GetFileName p = "jenkins-log.txt"
        || Path.GetFileName p = "jenkins-result.txt"
        || Path.GetFileName p = "script.sh"
        || Path.GetFileName p = "script.sh.copy"

    /// Hash a directory tree: sorted (relative path, content hash) pairs. Sorted
    /// because directory enumeration order is not a semantic property.
    let hashWorkspace (root: string) : string * (string * string) list =
        if not (Directory.Exists root) then
            sha256Text "", []
        else
            let entries =
                Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                |> Array.choose (fun full ->
                    let relative =
                        Path.GetRelativePath(root, full).Replace('\\', '/')

                    if isScaffolding relative then
                        None
                    else
                        let content =
                            try
                                sha256Hex (File.ReadAllBytes full)
                            with _ ->
                                "unreadable"

                        Some(relative, content))
                |> Array.sortBy fst
                |> Array.toList

            let manifest =
                entries |> List.map (fun (p, h) -> $"{p}\t{h}") |> String.concat "\n"

            sha256Text manifest, entries

    /// Normalise one output line so engine-specific decoration does not count as
    /// a semantic difference. Every rule here is a measured difference between
    /// the two engines, not a guess.
    let normaliseLine (line: string) : string option =
        let stripped =
            Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;]*[A-Za-z]", "")

        let t = stripped.Trim()

        if t = "" then
            None
        // Jenkins pipeline-graph annotations: pure structure, no output
        elif t.StartsWith "[Pipeline]" then None
        // Jenkins node/workspace banners
        elif t.StartsWith "Running on " || t.StartsWith "Running in " then None
        elif t.StartsWith "Started by " then None
        elif t.StartsWith "Resuming build" || t.StartsWith "Ready to run" then None
        elif t.StartsWith "Finished:" then None
        elif t.StartsWith "GitHub has been notified" then None
        // `sh` xtrace echo: Jenkins prints `+ cmd`, and so does a shell with -x.
        // The command text is not output, it is provenance.
        elif t.StartsWith "+ " then None
        // Jenkins prefixes workspace paths that differ by construction
        elif Text.RegularExpressions.Regex.IsMatch(t, @"^\[.*\] Running shell script$") then None
        else Some t

    let normaliseOutput (lines: string seq) : string list =
        lines |> Seq.choose normaliseLine |> List.ofSeq

    /// The exclusions above are part of the contract, so they are published with
    /// every receipt rather than buried in code.
    let comparisonContract =
        [ "compared: terminal result"
          "compared: ordered normalised output lines"
          "compared: canonical workspace hash over sorted (path, content-hash) pairs"
          "excluded: timestamps, ANSI escapes, blank lines"
          "excluded: [Pipeline] graph annotations, node/workspace banners, Started/Finished lines"
          "excluded: shell xtrace ('+ cmd') lines — provenance, not output"
          "excluded: .git, @tmp siblings, durable-task spool files, script.sh, *.pid"
          "not compared: wall-clock duration, log ordering across stdout/stderr, plugin banners" ]
