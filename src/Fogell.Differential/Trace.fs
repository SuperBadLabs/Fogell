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
      /// Engine-health observations that are PRINTED in the receipt but never
      /// compared: they describe the ENGINE's ability to check something (a /proc
      /// scan that failed), not the build. FG-103: an unavailable check must be
      /// said somewhere — and the receipt is where an engine talks about itself
      /// without inventing build output Jenkins does not print.
      EngineNotes: string list
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
    /// The timeout narration family. FG-102: these are EMITTED by Fogell in
    /// Jenkins' own wording (measured on 2.568.1 — `Timeout set to expire in 3
    /// sec`, `1 mo 0 days`, the Cancelling/Sending/Terminated/exceeded cluster)
    /// and therefore COMPARED as output, not suppressed. They remain
    /// reason-qualifying below: an aborted build whose only explanation is this
    /// cluster HAS explained itself.
    let isTimeoutNarration (t: string) =
        t.StartsWith "Timeout set to expire"
        || t.StartsWith "Timeout has been exceeded"
        || t.StartsWith "Cancelling nested steps"
        || t.StartsWith "Sending interrupt signal to process"

    let isDiagnosticLine (t: string) =
        t.StartsWith "ERROR:"
        // FG-044. Jenkins narrates credential masking as one line naming every bound
        // variable, joined with " or ". Emitting the same INFORMATION is parity; matching
        // that join word character for character would over-fit to plugin wording, the
        // thing this contract exists to avoid. Both engines say it; the wording is not
        // compared, and the load-bearing evidence is that the value never appears.
        || t.StartsWith "Masking supported pattern matches of "
        // FG-044b. Jenkins warns, in three lines, when a secret is interpolated into a
        // step argument via a Groovy GString. Fogell warns too — the security advice is
        // worth keeping — but matching three sentences and a URL verbatim would be
        // over-fitting to plugin wording. Both engines say it; the words are not compared.
        // NOTE: the secret-interpolation warning is NOT matched here. It is recognised by
        // CONTEXT in [normaliseOutput], because every line of it is text a build could
        // print. Adding it as four prefixes was the SIXTH instance of this class — written
        // in the same PR whose own tests forbid it.
        // FG-048b. Jenkins warns about an empty changelog when evaluating `changeset` or
        // `changelog` on a first build. Engine narration about its own evaluation, not
        // build output — and imitating the sentence would be over-fitting again.
        // The EXACT sentence, not a prefix. REGRESSION, caught by both reviewers: a broad
        // prefix match drops any user output beginning with these words and — because
        // diagnostic lines also set ReportedFailureReason — can mask an engine's silence on
        // a failed run, producing a FALSE PROVEN. That is precisely the `Terminated` defect
        // fixed earlier in this file; I reproduced it two rules later.
        || t = "Warning, empty changelog. Probably because this is the first build."
        // FG-049. A failing `post` step makes Jenkins print a Java exception and
        // stack trace into the build log. It is the engine explaining itself, which
        // is what this predicate is for — and matching a stack trace verbatim would
        // be over-fitting to a plugin's internals in the most extreme way available.
        || Text.RegularExpressions.Regex.IsMatch(t, @"^Error when executing \w+ post condition")
        // A pipeline-level timeout surfaces as this exception class in the log. Engine
        // narration about its own interrupt, same category as the lines above.
        // NOTE, third instance of one mistake: `WorkflowScript: \d+:`, a bare `^` and
        // `\d+ errors?` were added here last round to silence a Groovy compilation report.
        // Every one of them is USER-REPRODUCIBLE — a pipeline echoing "1 error" would lose
        // the line, and because this predicate also feeds ReportedFailureReason it could
        // mask an engine failing silently and yield a FALSE PROVEN. That is exactly the
        // defect fixed for `Terminated`, then again for the changelog warning, and I
        // recreated it while fixing the second one. They are removed rather than narrowed:
        // they existed only for a case that is no longer in the suite, so the machinery
        // was pure risk. The rule this file needs is that a pattern belongs here only if a
        // user's own output cannot plausibly match it.
        // The timeout plugin appends an opaque correlation id. It carries no
        // semantics and its value changes every run, so it could never be
        // compared even in principle.
        || Text.RegularExpressions.Regex.IsMatch(
            t,
            @"^(Also:\s+)?org\.jenkinsci\.plugins\.workflow\.actions\.ErrorAction\$ErrorId: [0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"
        )

    /// Normalise one output line so engine-specific decoration does not count as
    /// a semantic difference. Every rule here is a measured difference between
    /// the two engines, not a guess.
    /// FG-102: the engine's startup/header banners. A banner is recognised by its
    /// shape AND its context: on Jenkins every user-printed line follows an
    /// output-producing `[Pipeline]` step annotation (`echo`, `sh`, …), while the
    /// real header lines follow nothing or other header material. A spoofed
    /// banner therefore keeps its annotation and COMPARES; if the two engines
    /// disagree on such a line the receipt shows a divergence rather than a
    /// silent double-drop.
    let isPreambleBanner (t: string) =
        Text.RegularExpressions.Regex.IsMatch(t, @"^Running on .+ in (/|\$\{WORKSPACE\})")
        || ([ "MAX_SURVIVABILITY"; "SURVIVABLE_NONATOMIC"; "PERFORMANCE_OPTIMIZED" ]
            |> List.exists (fun lvl -> t = $"Running in Durability level: {lvl}"))
        || t.StartsWith "Started by user "
        || t.StartsWith "Started by timer"
        || t.StartsWith "Started by upstream "
        || t = "Started by remote host"
        || t.StartsWith "Started by an SCM change"
        || Text.RegularExpressions.Regex.IsMatch(t, @"^Resuming build at \d")
        || Text.RegularExpressions.Regex.IsMatch(t, @"^Ready to run at \d")

    /// The pipeline-graph annotation GRAMMAR: `[Pipeline] word`, braces with an
    /// optional stage label, `// word` closes, and the exact multiword boundary
    /// sentences. Applied only to a trace bearing REAL structure (see
    /// [normaliseOutputInner]): Fogell emits no annotations, so an
    /// annotation-shaped user line there stays visible and a Jenkins-side drop
    /// becomes a VISIBLE divergence, never a silent double-drop.
    let isGraphAnnotation (t: string) =
        t = "[Pipeline] Start of Pipeline"
        || t = "[Pipeline] End of Pipeline"
        || Text.RegularExpressions.Regex.IsMatch(t, @"^\[Pipeline\]( \{( \(.*\))?| \}| // [A-Za-z][A-Za-z0-9_]*| [A-Za-z][A-Za-z0-9_]*)?$")

    let normaliseLine (line: string) : string option =
        let stripped =
            Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;]*[A-Za-z]", "")

        let t = stripped.Trim()

        if t = "" then
            None

        // FG-049. `Post stage` is the declarative graph's label for the synthetic
        // stage that wraps a post section — the same category as [Pipeline]
        // annotations: structure, not output. Excluded rather than imitated,
        // because inventing Jenkins' internal narration is what went wrong with
        // the `[branch]` prefix in FG-036.
        elif t = "Post stage" then None
        // Jenkins node/workspace banners

        elif Text.RegularExpressions.Regex.IsMatch(t, @"^Finished: (SUCCESS|FAILURE|ABORTED|UNSTABLE|NOT_BUILT)$") then None
        elif
            t = "GitHub has been notified of this commit\u2019s build result"
            || t = "GitHub has been notified of this commit's build result"
        then
            None

        // Jenkins prefixes workspace paths that differ by construction
        elif Text.RegularExpressions.Regex.IsMatch(t, @"^\[.*\] Running shell script$") then None
        // Plugin banners: an artifact of which plugins this Jenkins has installed,
        // not of Jenkins' behaviour. `[Checks API] No suitable checks publisher
        // found.` appears purely because the checks plugin is present and
        // unconfigured.
        elif t = "[Checks API] No suitable checks publisher found." then None
        // Engine diagnostic wording — captured as ReportedFailureReason instead.
        elif isDiagnosticLine t then None
        else Some t

    /// REVIEW FIX (Codex P2, PR #12): `Terminated` was excluded by an
    /// unconditional text match, so a build whose own script printed that word
    /// silently lost the line — and a lost line on one side only is how a FALSE
    /// `PROVEN` happens. Jenkins emits it only as the second half of
    /// "Sending interrupt signal to process" / "Terminated", so it is excluded
    /// ONLY when such an interrupt was actually narrated earlier in the run.
    /// Everywhere else it is ordinary user output and is compared.
    /// Canonicalise the engine's own ABSOLUTE workspace path to `${'$'}{WORKSPACE}` —
    /// the newly compared xtrace expands engine-provided paths, and
    /// `+ test -d /each/engine's/root` is one command with two spellings, not two
    /// commands. Applied to every line: the same substitution an author's
    /// `$WORKSPACE` reference would produce on either side.
    let internal normaliseOutputInner (lines: string seq) : string list =
        // Two engine-narration shapes are recognised by CONTEXT, never by their text
        // alone, because a build can legitimately print either:
        //
        //   * `Terminated` — only when an interrupt was narrated on the previous line.
        //   * a Java exception head (`hudson.AbortException: …`) and the `at …(…)` frames
        //     under it — only when a frame actually FOLLOWS the head. A pipeline echoing
        //     an exception class name on its own keeps that line.
        //
        // This is the fifth defect of one class: a pattern a user's own output can match,
        // removed from the compared output AND counted as a reported failure reason, so an
        // engine failing silently could still read PROVEN. Six such patterns turned out to
        // be protecting nothing and were deleted (FG-002f); these three are load-bearing,
        // so they are gated instead.
        let all = lines |> Seq.toArray

        let clean (l: string) =
            Text.RegularExpressions.Regex.Replace(l, @"\x1b\[[0-9;]*[A-Za-z]", "").Trim()

        let isFrame (l: string) = l.StartsWith "at " && l.Contains "("

        let looksLikeExceptionHead (l: string) =
            Text.RegularExpressions.Regex.IsMatch(l, @"^[\w.$]+(Exception|Error)\b")

        // The secret-interpolation warning is a SEQUENCE — Jenkins emits head+body+tail,
        // Fogell emits the same head+body — and every line of it is text a build could
        // print on its own, so the head counts as narration only when the line that must
        // follow it does. Fogell once had a one-line wording of its own, recognised here
        // by shape ALONE; a build printing that shape was dropped from the comparison
        // unconditionally, so a case whose evidence was that line could go falsely
        // PROVEN. Both engines now speak the same sequence and sit under this one
        // contextual gate; there is no unconditional match left.
        let isWarnHead (l: string) = l.StartsWith "Warning: A secret was passed to"
        let isWarnBody (l: string) = l.StartsWith "Affected argument(s) used the following variable(s)"
        let isWarnTail (l: string) = l.StartsWith "See https://jenkins.io/redirect/groovy-string-interpolation"

        let mutable inStackTrace = false
        let mutable pastFirstOutputStep = false

        // Banner suppression applies only to a trace that HAS the Jenkins graph
        // annotations giving it context. Fogell emits none — so its banner-shaped
        // first line is ordinary output, and dropping it made identical runs
        // falsely diverge on same-text spoofs.
        // REAL structure is discriminated by the boundary sentence Jenkins always
        // prints, not by any bracketed shape — a lone spoofed `[Pipeline] echo` on
        // the Fogell side must not switch suppression on for its own trace.
        let hasAnnotations =
            all |> Array.exists (fun l -> clean l = "[Pipeline] Start of Pipeline")
        let mutable prevRaw = ""
        let mutable inSecretWarning = false

        [ for i in 0 .. all.Length - 1 do
            let raw = clean all[i]
            let next = if i + 1 < all.Length then clean all[i + 1] else ""

            // A head only opens the window when a frame really follows it.
            if looksLikeExceptionHead raw && isFrame next then inStackTrace <- true
            elif not (isFrame raw) then inStackTrace <- false

            let suppress =
                (isFrame raw && inStackTrace)
                || (hasAnnotations && isGraphAnnotation raw)
                // `dir()`'s banner, by CONTEXT: `Running in <abs path>` counts as the
                // banner only immediately after the `[Pipeline] dir` annotation — a
                // build echoing the same shape mid-run is compared, differing
                // workspace paths and all.
                || (Text.RegularExpressions.Regex.IsMatch(raw, @"^Running in (/|\$\{WORKSPACE\})")
                    && prevRaw.StartsWith "[Pipeline] dir")
                || (looksLikeExceptionHead raw && isFrame next)
                || (isWarnHead raw && isWarnBody next)
                || ((isWarnBody raw || isWarnTail raw) && inSecretWarning)

            inSecretWarning <-
                (isWarnHead raw && isWarnBody next)
                || (inSecretWarning && (isWarnBody raw || isWarnTail raw))

            // Header banners exist only BEFORE the first output-producing step of
            // the whole build — once any `echo`/`sh`/`bat`/`input` annotation has
            // appeared, every later line is build territory and banner shapes are
            // ordinary output (consecutive ones included: a per-line context made
            // the second of two identical spoofed lines vanish).
            if
                raw.StartsWith "[Pipeline] echo"
                || raw.StartsWith "[Pipeline] sh"
                || raw.StartsWith "[Pipeline] bat"
                || raw.StartsWith "[Pipeline] input"
            then
                pastFirstOutputStep <- true

            prevRaw <- raw

            if not suppress then
                match normaliseLine all[i] with
                | Some l ->
                    if not hasAnnotations || pastFirstOutputStep || not (isPreambleBanner l) then
                        yield l
                | None -> () ]

    /// The environment names whose ENGINE-INHERITED values are canonicalised to
    /// `${'$'}{NAME}` in each engine's own trace. The compared xtrace expands what a
    /// script references, and these differ between agents BY CONSTRUCTION — the
    /// same reason the workspace path does. Curated, not blanket: replacing every
    /// short env value would mangle unrelated output.
    let canonicalisedEnvNames =
        [ "WORKSPACE"; "PATH"; "HOME"; "HOSTNAME"; "USER"; "LOGNAME"; "SHELL"; "JAVA_HOME"; "TMPDIR"; "PWD" ]

    let internal normaliseOutputWithInner2
        (globalReplacements: (string * string) list)
        (traceOnlyReplacements: (string * string) list)
        (lines: string seq)
        : string list =
        let order rs =
            rs
            |> List.filter (fun ((v: string), _) -> v <> "" && v.Length >= 4)
            |> List.sortByDescending (fun ((v: string), _) -> v.Length)

        let orderedGlobal = order globalReplacements
        let orderedTrace = order traceOnlyReplacements

        let canonical (l: string) =
            let g =
                orderedGlobal |> List.fold (fun (acc: string) (v, token) -> acc.Replace(v, token)) l

            // ENV values rewrite only on xtrace rows — engine-generated lines where
            // expansions actually appear. Ordinary output keeps its literals, which
            // is what makes the replacement injective without parsing the script:
            // `echo /root` compares as text, `+ test -n /root` compares as the
            // expansion it is. (A build printing its own `+ `-shaped line joins the
            // stated mimicry residual; a script that CHANGES PS4 forfeits env
            // canonicalisation on its custom-prefixed rows and any inherited-value
            // difference there diverges VISIBLY — declared, fail-closed.)
            if Text.RegularExpressions.Regex.IsMatch(g.TrimStart(), @"^\++ ") then
                orderedTrace |> List.fold (fun (acc: string) (v, token) -> acc.Replace(v, token)) g
            else
                g

        normaliseOutputInner (lines |> Seq.map canonical)

    let normaliseOutput (lines: string seq) : string list = normaliseOutputInner lines

    /// `applyDurableShape` — the JENKINS side cannot know its own generated
    /// durable-script ids (they exist only inside its container), so its trace
    /// stabilises the full measured shape `@tmp/durable-<8hex>/script.sh` to
    /// `<id>`. The FOGELL side knows its EXACT generated ids and passes them as
    /// ordinary replacements instead, so a fogell-side spoof with a different id
    /// stays literal — a cross-engine spoof pair therefore DIVERGES visibly
    /// rather than collapsing to one token.
    let normaliseOutputShaped
        (applyDurableShape: bool)
        (globalReplacements: (string * string) list)
        (traceOnlyReplacements: (string * string) list)
        (lines: string seq)
        : string list =
        let shaped (l: string) =
            if applyDurableShape then
                Text.RegularExpressions.Regex.Replace(l, "(@tmp/durable-)[0-9a-f]{8}(/script\.sh)", "$1<id>$2")
            else
                l

        normaliseOutputWithInner2 globalReplacements traceOnlyReplacements (lines |> Seq.map shaped)

    let normaliseOutputWith (replacements: (string * string) list) (lines: string seq) : string list =
        normaliseOutputWithInner2 replacements [] lines


    /// `Terminated` on its own is only a reason when an interrupt was narrated with it;
    /// see [normaliseOutput].
    ///
    /// REVIEW FIX (Codex, PR #16 round 9): moving exception heads and stack frames out of
    /// [isDiagnosticLine] and into a CONTEXTUAL gate left this function unable to see
    /// them. A Jenkins failure explained ONLY by a stack trace would then report NO
    /// reason while Fogell's `ERROR:` reported one — a DiagnosticSilence divergence on an
    /// otherwise matching run. The same context detection has to serve both.
    let reportedFailureReason (lines: string seq) : bool =
        let all = lines |> Seq.toArray

        let clean (l: string) =
            Text.RegularExpressions.Regex.Replace(l, @"\x1b\[[0-9;]*[A-Za-z]", "").Trim()

        let isFrame (l: string) = l.StartsWith "at " && l.Contains "("

        let looksLikeExceptionHead (l: string) =
            Text.RegularExpressions.Regex.IsMatch(l, @"^[\w.$]+(Exception|Error)\b")

        let hasStackTrace =
            all
            |> Array.mapi (fun i l ->
                let raw = clean l
                let next = if i + 1 < all.Length then clean all[i + 1] else ""
                looksLikeExceptionHead raw && isFrame next)
            |> Array.exists id

        hasStackTrace
        || (all
            |> Array.map clean
            |> Array.exists (fun l -> isDiagnosticLine l || isTimeoutNarration l))

    /// The exclusions above are part of the contract, so they are published with
    /// every receipt rather than buried in code.
    let comparisonContract =
        [ "compared: terminal result"
          "compared: ordered normalised output lines"
          "compared: canonical workspace hash over sorted (path, content-hash) pairs"
          "excluded: timestamps, ANSI escapes, blank lines"
          "excluded: [Pipeline] graph annotations, 'Post stage' label, node/workspace banners, Started/Finished lines"
          "compared: shell xtrace ('+ cmd') lines — BOTH engines run `sh -xe`, so the"
          "  trace is identical emitted output, continuations included (retires FG-002c)"
          "excluded: .git, @tmp siblings, durable-task spool files, script.sh, *.pid"
          "excluded: plugin banners such as [Checks API] — an artifact of which plugins are installed"
          "compared as a BOOLEAN, not text: whether a failure reason was reported"
          "  (applies to failure/aborted only — an unstable build is explained by its test report)"
          "  (Jenkins' wording comes from whichever plugin implements the step;"
          "   matching it verbatim would over-fit to a plugin string. Silence is the defect.)"
          "excluded: credential-masking narration — both engines announce it, wording differs"
          "engine notes: printed in the receipt, never compared — the engine reporting"
          "  on its own checks (e.g. an unavailable /proc scan), not on the build"
          "RULE (FG-102): nothing is excluded on wording alone. Every exclusion above is"
          "  context-gated, emitted identically by both engines, or an exact measured"
          "  sentence — see docs/REVIEW_CHECKLIST.md. A build printing narration-like"
          "  text is COMPARED, and tests carry look-alike rows proving it."
          "excluded: compile/evaluation rejection narration — Jenkins refuses an invalid"
          "  pipeline at COMPILE time, Fogell when it evaluates the stage gate; both fail with"
          "  the same workspace, and comparing a compiler's error layout is over-fitting"
          "compared: timeout narration — both engines emit Jenkins' wording (set-to-expire"
          "  banner, Cancelling/Sending/Terminated, Timeout has been exceeded) and the"
          "  sentences ALSO count as the abort's reported reason"
          "excluded: `Failed in branch <name>` and ERROR-class reason lines — counted as"
          "  the reported reason; the wording comes from whichever plugin implements the step"
          "not compared: wall-clock duration, log ordering across stdout/stderr, diagnostic wording" ]
