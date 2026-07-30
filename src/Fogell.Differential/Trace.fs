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
      WorkspaceFiles: (string * string) list
      /// FG-036. True when the pipeline contained a `parallel` block, which is a
      /// property of the SCRIPT rather than of either engine's behaviour. Only
      /// the side that parses (Fogell) can set it; Compare takes the disjunction.
      /// It exists to trigger the documented output-ordering relaxation.
      Concurrent: bool
      /// Whether the engine reported a reason for a non-success.
      ///
      /// The exact wording is NOT compared: Jenkins' text comes from whichever
      /// plugin implements the step ("'x' doesn't match anything", with typographic
      /// quotes), and matching it character for character would be over-fitting to
      /// a plugin string rather than testing semantics. What must agree is that
      /// both engines told the user *something* about why the build failed —
      /// silence is the actual defect (JB-DUR-005).
      ReportedFailureReason: bool }

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

    /// FG-002b. Hash a workspace that lives somewhere this process cannot see,
    /// by running a caller-supplied command that prints `<sha256>  <path>` lines.
    ///
    /// The same [isScaffolding] filter and the same sorted-manifest hash are
    /// applied, so a remote hash and a local hash are computed identically. If
    /// they were not, a matching pair would prove nothing.
    let collectRemote (command: string) : string * (string * string) list =
        try
            let psi = Diagnostics.ProcessStartInfo("/bin/sh")
            psi.ArgumentList.Add "-c"
            psi.ArgumentList.Add command
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false

            use proc = Diagnostics.Process.Start psi
            let out = proc.StandardOutput.ReadToEnd()
            proc.WaitForExit 60_000 |> ignore

            let entries =
                out.Replace("\r\n", "\n").Split '\n'
                |> Array.choose (fun line ->
                    // `sha256sum` output: "<hash>  <path>"
                    let parts = line.Split("  ", 2, StringSplitOptions.None)

                    if parts.Length <> 2 then
                        None
                    else
                        let hash = parts[0].Trim()
                        let relative = parts[1].Trim().TrimStart('.', '/').Replace('\\', '/')

                        if hash = "" || relative = "" || isScaffolding relative then
                            None
                        else
                            Some(relative, hash))
                |> Array.sortBy fst
                |> Array.toList

            let manifest =
                entries |> List.map (fun (p, h) -> $"{p}\t{h}") |> String.concat "\n"

            sha256Text manifest, entries
        with _ ->
            "not-collected", []

    /// Lines in which an engine explains a failure. Their presence is compared;
    /// their wording is not.
    let isDiagnosticLine (t: string) =
        t.StartsWith "ERROR:"
        || t.StartsWith "FATAL:"
        // FG-034/036. Interrupt narration. MEASURED, not assumed: a `timeout`
        // makes Jenkins print "Timeout set to expire in 3 sec / Cancelling
        // nested steps due to timeout / Sending interrupt signal to process /
        // Terminated / Timeout has been exceeded", and a failFast parallel prints
        // "Failed in branch <name>". None of it is step output — it is the engine
        // explaining what it did to the step, which is exactly the category this
        // predicate exists for. Comparing the sentences verbatim would over-fit
        // to timeout-plugin wording; what must agree is that SOMETHING was said.
        || t.StartsWith "Timeout set to expire"
        || t.StartsWith "Timeout has been exceeded"
        || t.StartsWith "Cancelling nested steps"
        || t.StartsWith "Sending interrupt signal to process"
        || t = "Terminated"
        || t.StartsWith "Failed in branch "
        || t.StartsWith "Aborted by "
        // The timeout plugin appends an opaque correlation id. It carries no
        // semantics and its value changes every run, so it could never be
        // compared even in principle.
        || t.Contains "workflow.actions.ErrorAction$ErrorId"
        || t.Contains "doesn\u2019t match anything"
        || t.Contains "doesn't match anything"
        || Text.RegularExpressions.Regex.IsMatch(t, @"^No artifacts found")
        || Text.RegularExpressions.Regex.IsMatch(t, @"^\d+ of \d+ test\(s\) failed$")

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
        // Plugin banners: an artifact of which plugins this Jenkins has installed,
        // not of Jenkins' behaviour. `[Checks API] No suitable checks publisher
        // found.` appears purely because the checks plugin is present and
        // unconfigured.
        elif Text.RegularExpressions.Regex.IsMatch(t, @"^\[[A-Za-z][A-Za-z ]*(API|Plugin)\]") then None
        // Engine diagnostic wording — captured as ReportedFailureReason instead.
        elif isDiagnosticLine t then None
        else Some t

    let normaliseOutput (lines: string seq) : string list =
        lines |> Seq.choose normaliseLine |> List.ofSeq

    /// Did the engine explain itself? Computed over RAW lines, before the
    /// diagnostic-stripping normaliser removes them.
    let reportedFailureReason (lines: string seq) : bool =
        lines
        |> Seq.map (fun l -> Text.RegularExpressions.Regex.Replace(l, @"\x1b\[[0-9;]*[A-Za-z]", "").Trim())
        |> Seq.exists isDiagnosticLine

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
          "excluded: plugin banners such as [Checks API] — an artifact of which plugins are installed"
          "compared as a BOOLEAN, not text: whether a failure reason was reported"
          "  (applies to failure/aborted only — an unstable build is explained by its test report)"
          "  (Jenkins' wording comes from whichever plugin implements the step;"
          "   matching it verbatim would over-fit to a plugin string. Silence is the defect.)"
          "excluded: engine interrupt narration (timeout/abort/branch-failure lines) —"
          "  counted as a reported reason instead, since it explains the engine, not the step"
          "not compared: wall-clock duration, log ordering across stdout/stderr, diagnostic wording" ]
