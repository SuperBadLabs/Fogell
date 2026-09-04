namespace Fogell.Differential

open System
open System.IO
open System.Security.Cryptography

/// Whether user Pipeline code began executing. This is deliberately independent
/// of workspace presence: Jenkins can retain build 1's workspace when build 2 is
/// refused by the compiler before its first `[Pipeline]` annotation.
type ExecutionDisposition =
    | ExecutedOrRuntime
    | RefusedBeforeExecution

/// FG-002. The canonical form both engines are reduced to before comparison.
///
/// The hard part of a differential harness is deciding what "the same" means.
/// Comparing raw console output is hopeless — timestamps, node names, plugin
/// banners and ANSI codes all differ and none of it is semantics. So a run is
/// reduced to three things that *are* semantics:
///
///   1. the terminal result
///   2. the ordered sequence of observable step outputs — EXCEPT for a CONCURRENT
/// (parallel) build, whose outputs compare as a MULTISET because branch
/// interleaving is not a difference between the engines (FG-151)
///   3. a canonical hash of the workspace the run produced
///
/// Anything outside those three is deliberately not compared, and the reasons
/// are recorded here rather than left implicit.
type Trace =
    { /// Absent from legacy receipts because ordinary execution is the historical
      /// default. Refusals render and seal one exact marker.
      Disposition: ExecutionDisposition
      /// success | failure | aborted | unstable
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
      /// Whether the console carried `timestamps()` prefixes.
      ///
      /// Jenkins stamps BUILD-OUTPUT lines, not its own annotations or banners
      /// (measured). The prefix TEXT cannot be compared — two engines read two clocks — so
      /// `normaliseLine` strips it and the contract has always claimed
      /// "excluded: timestamps". That claim was UNIMPLEMENTED until FG-053: no
      /// code stripped one, and it read as true only because no case used the
      /// option. Measured on Jenkins 2.568.1, a build-output line becomes
      /// `[2026-08-03T03:54:07.729Z] + echo first`.
      ///
      /// Stripping alone would make the exclusion dishonest in the other
      /// direction — an engine that ignored `timestamps()` entirely would
      /// compare equal to one that honoured it. So COVERAGE is compared, as
      /// `none` / `partial` / `all`: the wording is the plugin's, the fact is
      /// the engine's. (This said "as a boolean" after the code had moved on —
      /// the same sentence, in its seventh place on this branch.)
      /// (COMPARABLE lines carrying a `timestamps()` prefix, comparable lines).
      ///
      /// A COVERAGE PAIR, not a boolean, and the boolean was my own bug: under
      /// `Seq.exists` an engine that stamped ONE line compared equal to one that
      /// stamped every line — the same partial-implementation hole the enable
      /// timing had, one level up.
      ///
      /// COMPARED AS `none` / `partial` / `all` AND NOTHING ELSE. The counts are
      /// printed for a reader and never compared: two engines do not print the
      /// same number of lines, so comparing them would manufacture divergences
      /// out of an accident of formatting.
      ///
      /// FG-118. Both counts come from the SAME final survivor set. Timestamp
      /// provenance is attached before decoration is stripped and carried through
      /// the contextual shaping pipeline, so a stamped annotation that is later
      /// suppressed cannot offset an unstamped line that is actually compared.
      /// `all` therefore means every compared survivor carried the prefix; it says
      /// nothing about suppressed Jenkins narration or other raw console lines.
      Timestamps: int * int

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

    type private WorkspaceEntry =
        | WorkspaceFile of path: string * hash: string
        | EmptyLeafDirectory of path: string

    // A readable receipt needs a hash-shaped value for a directory row. The value
    // is presentation only; the canonical directory record below is what binds the
    // workspace hash. A trailing slash makes the row impossible to confuse with a
    // file path.
    let private emptyDirectoryDisplayHash = sha256Text "\u0000D"

    let private entryPath = function
        | WorkspaceFile(path, _) -> path
        | EmptyLeafDirectory path -> path

    // The committed emitter uses `LC_ALL=C sort -z` over UTF-8 filesystem bytes.
    // Validate that wire order exactly. Canonical reduction below still uses the
    // historical .NET ordinal path order, preserving v1 file ordering even for
    // the narrow supplementary-plane ordering difference between UTF-8 and UTF-16.
    let private compareWirePath (left: string) (right: string) =
        Array.compareWith compare (Text.Encoding.UTF8.GetBytes left) (Text.Encoding.UTF8.GetBytes right)

    let private validateWorkspacePath (path: string) =
        let segments = path.Split '/'

        if
            path = ""
            || path.StartsWith "/"
            || path.EndsWith "/"
            || path.Contains '\\'
            || path |> Seq.exists Char.IsControl
            || Text.RegularExpressions.Regex.IsMatch(path, "^[A-Za-z]:")
            || segments |> Array.exists (fun segment -> segment = "" || segment = "." || segment = "..")
        then
            invalidOp "workspace path is not canonical"

        path

    let private finishWorkspace (entries: WorkspaceEntry list) =
        let ordered = entries |> List.sortWith (fun a b -> String.CompareOrdinal(entryPath a, entryPath b))

        // Compatibility is deliberate: every file record is exactly the v1
        // `<path><TAB><hash>` byte sequence and file order is unchanged. Only an
        // empty leaf adds a v2 record. NUL cannot occur in a filesystem path, so
        // this tag cannot collide with a file record; base64 keeps the new record
        // unambiguous even when a path contains whitespace.
        // Keep the established `manifest` identifier live: the repository's
        // stale-reference audit tracks deleted identifiers lexically, and this
        // remains the canonical workspace manifest despite its v2 directory rows.
        let manifest =
            ordered
            |> List.map (function
                | WorkspaceFile(path, hash) -> $"{path}\t{hash}"
                | EmptyLeafDirectory path ->
                    "\u0000D\t" + Convert.ToBase64String(Text.Encoding.UTF8.GetBytes path))
            |> String.concat "\n"

        let visible =
            ordered
            |> List.map (function
                | WorkspaceFile(path, hash) -> path, hash
                | EmptyLeafDirectory path -> path + "/", emptyDirectoryDisplayHash)

        sha256Text manifest, visible

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

    /// Hash a directory tree: files plus physical empty leaf directories. Directory
    /// links are never followed and a link to an empty directory is not an empty
    /// directory record. Sorted because enumeration order is not semantic.
    let hashWorkspace (root: string) : string * (string * string) list =
        if not (Directory.Exists root) then
            sha256Text "", []
        elif (File.GetAttributes root).HasFlag FileAttributes.ReparsePoint then
            "not-collected", []
        else
            let rec visit (directory: string) =
                Directory.EnumerateFileSystemEntries directory
                |> Seq.toArray
                |> Array.toList
                |> List.collect (fun full ->
                    let relative =
                        Path.GetRelativePath(root, full).Replace('\\', '/') |> validateWorkspacePath
                    let attrs = File.GetAttributes full

                    if attrs.HasFlag FileAttributes.ReparsePoint then
                        []
                    elif attrs.HasFlag FileAttributes.Directory then
                        if isScaffolding (relative + "/") then
                            []
                        else
                            let children = Directory.EnumerateFileSystemEntries full |> Seq.toArray

                            if children.Length = 0 then
                                [ EmptyLeafDirectory relative ]
                            else
                                visit full
                    elif isScaffolding relative then
                        []
                    else
                        let content =
                            try
                                sha256Hex (File.ReadAllBytes full)
                            with _ ->
                                "unreadable"

                        [ WorkspaceFile(relative, content) ])

            finishWorkspace (visit root)

    /// FG-002b/FG-173. Hash a workspace that lives somewhere this process cannot
    /// see. The collector protocol is deliberately strict and versioned:
    ///
    ///   FOGELL-WORKSPACE-MANIFEST<TAB>2<LF>
    ///   F<TAB><64 lowercase hex><TAB><canonical base64 UTF-8 path><LF>
    ///   D<TAB><canonical base64 UTF-8 path><LF>
    ///   END<TAB><decimal record count><LF>
    ///
    /// Records are in strict UTF-8 bytewise decoded-path order. Paths are relative,
    /// slash-normalised, non-control, non-dot-segment spellings. The framing is
    /// validated but intentionally not hashed: file-only canonical bytes stay v1
    /// compatible. Any malformed/truncated/duplicate/conflicting response or a
    /// non-zero collector exit fails closed as `not-collected`.
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
            let stdout = proc.StandardOutput.ReadToEndAsync()
            let stderr = proc.StandardError.ReadToEndAsync()

            if not (proc.WaitForExit 60_000) then
                proc.Kill true
                invalidOp "workspace collector timed out"

            if
                not (
                    Threading.Tasks.Task.WaitAll(
                        [| stdout :> Threading.Tasks.Task; stderr :> Threading.Tasks.Task |],
                        5_000
                    )
                )
            then
                invalidOp "workspace collector output did not close"

            let out = stdout.Result
            stderr.Result |> ignore

            if proc.ExitCode <> 0 then
                invalidOp $"workspace collector exited {proc.ExitCode}"

            if out.Contains '\r' || not (out.EndsWith "\n") || out.EndsWith "\n\n" then
                invalidOp "workspace collector framing is invalid"

            let lines = out.Split '\n' |> Array.rev |> Array.tail |> Array.rev

            if lines.Length < 2 || lines[0] <> "FOGELL-WORKSPACE-MANIFEST\t2" then
                invalidOp "workspace collector version is invalid"

            let trailer = lines[lines.Length - 1]
            let trailerMatch = Text.RegularExpressions.Regex.Match(trailer, "^END\\t(0|[1-9][0-9]*)$")

            if not trailerMatch.Success then
                invalidOp "workspace collector trailer is invalid"

            let recordLines = lines[1 .. lines.Length - 2]
            let expectedCount = Int32.Parse trailerMatch.Groups[1].Value

            if recordLines.Length <> expectedCount then
                invalidOp "workspace collector count is invalid"

            let utf8 = Text.UTF8Encoding(false, true)

            let decodePath encoded =
                if
                    encoded = ""
                    || encoded.Length % 4 <> 0
                    || not (Text.RegularExpressions.Regex.IsMatch(encoded, "^[A-Za-z0-9+/]+={0,2}$"))
                then
                    invalidOp "workspace collector path encoding is invalid"

                let bytes = Convert.FromBase64String encoded

                if Convert.ToBase64String bytes <> encoded then
                    invalidOp "workspace collector path encoding is not canonical"

                utf8.GetString bytes |> validateWorkspacePath

            let mutable previous: string option = None
            let seen = Collections.Generic.HashSet<string>(StringComparer.Ordinal)

            let hasObservedLeafAncestor (path: string) =
                let mutable slash = path.IndexOf '/'
                let mutable found = false

                while slash >= 0 && not found do
                    found <- seen.Contains(path.Substring(0, slash))
                    slash <- path.IndexOf('/', slash + 1)

                found

            let entries =
                recordLines
                |> Array.map (fun line ->
                    let fields = line.Split '\t'

                    let entry =
                        match fields with
                        | [| "F"; hash; encoded |]
                            when Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-f]{64}$") ->
                            WorkspaceFile(decodePath encoded, hash)
                        | [| "D"; encoded |] -> EmptyLeafDirectory(decodePath encoded)
                        | _ -> invalidOp "workspace collector record is invalid"

                    let path = entryPath entry

                    if seen.Contains path then
                        invalidOp "workspace collector path is duplicated or conflicting"

                    match previous with
                    | Some prior when compareWirePath prior path >= 0 ->
                        invalidOp "workspace collector records are not strictly sorted"
                    | _ -> ()

                    // Every F is a file and every emitted D is an EMPTY leaf. An
                    // earlier leaf therefore cannot be an ancestor of this row.
                    // Strict ordering guarantees ancestors precede descendants,
                    // but they need not be adjacent (`a`, `a-foo`, `a/b`), so the
                    // check covers every slash-boundary prefix against all seen rows.
                    if hasObservedLeafAncestor path then
                        invalidOp "workspace collector contains an impossible leaf hierarchy"

                    seen.Add path |> ignore
                    previous <- Some path

                    entry)
                |> Array.choose (fun entry ->
                    let scaffolding =
                        match entry with
                        | WorkspaceFile(path, _) -> isScaffolding path
                        | EmptyLeafDirectory path -> isScaffolding (path + "/")

                    if scaffolding then None else Some entry)
                |> Array.toList

            finishWorkspace entries
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

    /// FG-243. When the build ran on an agent the controller prints this marker
    /// (and the exception head under it) behind a `hudson.remoting.ProxyException:`
    /// wrapper; the wrapper is remoting's, not the build's, and is accepted here —
    /// only behind the `Also:` prefix it was measured with (Codex, PR #388): the
    /// bare wrapped form is unmeasured and stays a build's own line.
    let private isErrorActionIdDiagnostic (t: string) =
        Text.RegularExpressions.Regex.IsMatch(
            t,
            @"^(Also:\s+(hudson\.remoting\.ProxyException:\s+)?)?org\.jenkinsci\.plugins\.workflow\.actions\.ErrorAction\$ErrorId: [0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"
        )

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
        || isErrorActionIdDiagnostic t

    let private isStackFrame (line: string) =
        line.StartsWith "at " && line.Contains "("

    /// A Java exception head, optionally introduced by the standard nested-cause
    /// prefix. This helper deliberately does not decide whether the line is engine
    /// narration: only a following stack frame supplies that context.
    let private tryStackTraceHeadMessage (line: string) =
        let matched =
            Text.RegularExpressions.Regex.Match(
                line,
                @"^(?:Caused:\s+)?[\w.$]+(?:Exception|Error)\b(?::\s*(.*))?$"
            )

        if matched.Success then Some matched.Groups[1].Value else None

    /// FG-243. How many lines a Java exception MESSAGE may span between its head
    /// and the first `at …(…)` frame. `PatternSyntaxException` prints three
    /// (description, the pattern, a caret); an `AssertionError` or a multi-line
    /// `error()` message prints as many as the message holds. Bounded so a
    /// build that echoes an exception name and then, much later, a frame-shaped
    /// line does not lose the pages in between.
    let stackTraceMessageContinuationBound = 8

    /// FG-243. A frame that may confirm a head ACROSS message lines must look
    /// like the JVM's own: an optional module (`java.base/`) or plugin loader
    /// (`PluginClassLoader for x//`) prefix, a class path of at least three
    /// dotted segments, and a source location in parentheses — `Foo.java:12`,
    /// `Unknown Source`, `Native Method`, or any identifier with an optional `:line` (a Groovy
    /// `Script:12`; the location is shape-checked only, so `at a.b.c(d)` also
    /// confirms — the three-segment class path carries the guard). The looser
    /// [isStackFrame] stays the rule for a frame on the very next line — the
    /// FG-002f known limit — because widening the window with the loose shape
    /// would swallow a build that prints an exception name, a line of its own
    /// and then `at index.js(10)`, the look-alike row that test holds; a
    /// package-less frame such as `at WorkflowScript.run(WorkflowScript:49)` is
    /// not a confirmer either. The verifier's probe against the first cut of
    /// this rule found every `java.base/` frame rejected, so the receipt was
    /// confirmed after six message lines by a Groovy frame instead of after two
    /// by the JDK's — the module prefix is here because of it.
    let private isJavaStackFrame (line: string) =
        Text.RegularExpressions.Regex.IsMatch(
            line,
            @"^at (?:[\w$.<>/ -]+//|[\w.]+/)?[\w$]+(?:\.[\w$<>]+){2,}\((?:Unknown Source|Native Method|[A-Za-z_$][\w$.-]*(?::\d+)?)\)$"
        )

    /// FG-243. If [cleaned].[i] is an exception head that a frame confirms — any
    /// frame on the very next line, or two consecutive JVM-shaped ones after at most
    /// [stackTraceMessageContinuationBound] non-empty, non-annotation, non-head
    /// continuation lines — the number of continuation lines the message spans;
    /// `None` when no frame confirms it. The old rule was the `Some 0` case only,
    /// so a `PatternSyntaxException` head (three message lines) was compared as
    /// output and counted as no reason.
    let private stackTraceContinuation (cleaned: string[]) (i: int) : int option =
        if Option.isNone (tryStackTraceHeadMessage cleaned[i]) then
            None
        else
            let rec scan k =
                let j = i + 1 + k

                if j >= cleaned.Length || k > stackTraceMessageContinuationBound then
                    None
                elif k = 0 && isStackFrame cleaned[j] then
                    Some k
                elif k > 0 && isJavaStackFrame cleaned[j] && j + 1 < cleaned.Length && isJavaStackFrame cleaned[j + 1] then
                    // across a span, ONE frame-shaped line is thin evidence (Codex, PR
                    // #390): two consecutive JVM-shaped frames are required, which
                    // every measured trace has by the dozen
                    Some k
                elif cleaned[j] = "" || cleaned[j].StartsWith "[Pipeline]" || Option.isSome (tryStackTraceHeadMessage cleaned[j]) then
                    None
                else
                    scan (k + 1)

            scan 0

    let private startsStackTrace (cleaned: string[]) (i: int) =
        Option.isSome (stackTraceContinuation cleaned i)

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
    /// [normaliseOutputInnerWhen]): Fogell emits no annotations, so an
    /// annotation-shaped user line there stays visible and a Jenkins-side drop
    /// becomes a VISIBLE divergence, never a silent double-drop.
    let isGraphAnnotation (t: string) =
        t = "[Pipeline] Start of Pipeline"
        || t = "[Pipeline] End of Pipeline"
        || Text.RegularExpressions.Regex.IsMatch(t, @"^\[Pipeline\]( \{( \(.*\))?| \}| // [A-Za-z][A-Za-z0-9_]*| [A-Za-z][A-Za-z0-9_]*)?$")

    /// `options { timestamps() }` prefixes console lines with an ISO-8601
    /// instant in brackets. Measured, Jenkins 2.568.1:
    ///     [2026-08-03T03:54:07.729Z] + echo first
    ///
    /// NOT every line, which an earlier version of this comment asserted from a
    /// single divergence message and was wrong about twice: Jenkins stamps what
    /// the BUILD prints, not its own [Pipeline] annotations, banners or
    /// Started/Finished rows. Measured 2/21 raw lines on a two-step build.
    /// Anchored and shaped: a build printing `[not a timestamp] x` keeps its
    /// line, because an exclusion that swallows arbitrary bracketed text would
    /// hide real output — the failure FG-102's rule exists to prevent.
    let private timestampPrefix =
        Text.RegularExpressions.Regex(
            @"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z\]\s",
            Text.RegularExpressions.RegexOptions.Compiled)

    /// Did this line carry a `timestamps()` prefix? Feeds [Trace.Timestamps],
    /// which IS compared — stripping without comparing presence would let an
    /// engine that ignores the option pass against one that honours it.
    let private stripAnsi (l: string) =
        Text.RegularExpressions.Regex.Replace(l, @"\x1b\[[0-9;]*[A-Za-z]", "")

    /// Did this line carry a `timestamps()` prefix? Feeds [Trace.Timestamps], the coverage pair,
    /// which IS compared.
    ///
    /// COLUMN ZERO, no `TrimStart`. Trimming first meant ordinary indented
    /// output — `   [2026-08-03T03:54:07.729Z] value`, a build echoing a
    /// timestamp of its own — read as engine decoration and was stripped,
    /// hiding real output while the comment above claimed the rule was anchored.
    /// An engine's prefix is always at column zero; a build's is not.
    let hasTimestampPrefix (line: string) =
        timestampPrefix.IsMatch(stripAnsi line)

    /// ANSI escapes AND a `timestamps()` prefix — every decoration an engine may
    /// put around a line that says nothing about what the build did.
    ///
    /// ONE helper, used by line normalisation, `[Pipeline]` annotation
    /// detection, suppression context and `reportedFailureReason`. Two private
    /// `clean` functions stripped ANSI only, so with the option on, annotations
    /// went unsuppressed and a timestamped `ERROR:` stopped counting as a
    /// reported failure reason — the fix for `normaliseLine` not reaching its
    /// siblings. The same half-fix shape as the audit's extraction/survivor
    /// drift, and the same answer: share the rule instead of repeating it.
    /// none / partial / all, and the ONLY place this rule lives. Three copies
    /// existed — the comparison, the seal and the receipt body — and two of them
    /// disagreed about `stamped > total`, so a proof could compare one fact and
    /// seal another.
    /// The one validity rule for a timestamp-count pair, shared by comparison,
    /// rendering and receipt extraction. Production derives both values from one
    /// survivor list, but externally supplied records and receipts are hostile.
    let timestampCountsValid (stamped: int, total: int) =
        stamped >= 0 && total >= 0 && stamped <= total

    let timestampCoverage ((stamped, total) as counts) =
        if not (timestampCountsValid counts) then "invalid"
        elif stamped = 0 then "none"
        elif stamped = total then "all"
        else "partial"

    /// `stripTimestamps` — whether the SCRIPT declared `options { timestamps() }`.
    ///
    /// Conditional, and the first version was not: it removed a timestamp-shaped
    /// prefix from every compared line of every case, so a build printing
    /// `[2026-08-03T03:54:07.729Z] value` at column zero had it stripped and two
    /// DIFFERENT user timestamps compared equal. The comment claimed anchoring
    /// protected exactly that — but anchoring only tells column zero from an
    /// indent, and cannot tell the engine's decoration from the build's own
    /// output. Only the script knows, so the script decides.
    let stripDecoration (stripTimestamps: bool) (l: string) =
        if stripTimestamps then timestampPrefix.Replace(stripAnsi l, "") else stripAnsi l

    let normaliseLineWhen (stripTimestamps: bool) (line: string) : string option =
        let stripped =
            Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;]*[A-Za-z]", "")

        // the prefix goes BEFORE every other rule, or each of them would have to
        // know about it: `Post stage` and `Finished: SUCCESS` are matched
        // exactly, and a timestamped copy would sail past both.
        let t =
            (if stripTimestamps then timestampPrefix.Replace(stripped, "") else stripped)
                .Trim()

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
    type private TaggedLine =
        { Text: string
          HadTimestampPrefix: bool }

    let private normaliseOutputInnerTaggedWhen
        (stripTimestamps: bool)
        (lines: TaggedLine seq)
        : TaggedLine list =
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

        let clean (l: TaggedLine) = (stripDecoration stripTimestamps l.Text).Trim()

        let cleaned = all |> Array.map clean

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
        // FG-243. Message-continuation lines still owed to the head that opened the window.
        let mutable continuationLeft = 0
        let mutable pastFirstOutputStep = false

        // Banner suppression applies only to a trace that HAS the Jenkins graph
        // annotations giving it context. Fogell emits none — so its banner-shaped
        // first line is ordinary output, and dropping it made identical runs
        // falsely diverge on same-text spoofs.
        // REAL structure is discriminated by the boundary sentence Jenkins always
        // prints, not by any bracketed shape — a lone spoofed `[Pipeline] echo` on
        // the Fogell side must not switch suppression on for its own trace.
        let hasAnnotations =
            cleaned |> Array.exists ((=) "[Pipeline] Start of Pipeline")

        let mutable prevRaw = ""
        let mutable inSecretWarning = false

        [ for i in 0 .. all.Length - 1 do
            let raw = cleaned[i]
            let next = if i + 1 < cleaned.Length then cleaned[i + 1] else ""

            let headContinuation = if continuationLeft > 0 then None else stackTraceContinuation cleaned i
            let headStartsTrace = Option.isSome headContinuation
            let isContinuation = continuationLeft > 0

            // A head only opens the window when a frame really follows it — directly,
            // or after the bounded message continuation the head owes (FG-243).
            if headStartsTrace then
                inStackTrace <- true
                continuationLeft <- headContinuation.Value
            elif isContinuation then
                continuationLeft <- continuationLeft - 1
            elif not (isStackFrame raw) then
                inStackTrace <- false

            let suppress =
                (isStackFrame raw && inStackTrace)
                || isContinuation
                || (hasAnnotations && isGraphAnnotation raw)
                // `dir()`'s banner, by CONTEXT: `Running in <abs path>` counts as the
                // banner only immediately after the `[Pipeline] dir` annotation — a
                // build echoing the same shape mid-run is compared, differing
                // workspace paths and all.
                || (Text.RegularExpressions.Regex.IsMatch(raw, @"^Running in (/|\$\{WORKSPACE\})")
                    && prevRaw.StartsWith "[Pipeline] dir")
                || headStartsTrace
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
                match normaliseLineWhen stripTimestamps all[i].Text with
                | Some l ->
                    if not hasAnnotations || pastFirstOutputStep || not (isPreambleBanner l) then
                        yield
                            { Text = l
                              HadTimestampPrefix = all[i].HadTimestampPrefix }
                | None -> () ]

    let internal normaliseOutputInnerWhen (stripTimestamps: bool) (lines: string seq) : string list =
        lines
        |> Seq.map (fun line ->
            { Text = line
              HadTimestampPrefix = stripTimestamps && hasTimestampPrefix line })
        |> normaliseOutputInnerTaggedWhen stripTimestamps
        |> List.map (fun line -> line.Text)

    /// The environment names whose ENGINE-INHERITED values are canonicalised to
    /// `${'$'}{NAME}` in each engine's own trace. The compared xtrace expands what a
    /// script references, and these differ between agents BY CONSTRUCTION — the
    /// same reason the workspace path does. Curated, not blanket: replacing every
    /// short env value would mangle unrelated output.
    let canonicalisedEnvNames =
        [ "WORKSPACE"; "PATH"; "HOME"; "HOSTNAME"; "USER"; "LOGNAME"; "SHELL"; "JAVA_HOME"; "TMPDIR"; "PWD" ]

    let private normaliseOutputWithInnerTagged2
        (stripTimestamps: bool)
        (globalReplacements: (string * string) list)
        (traceOnlyReplacements: (string * string) list)
        (lines: TaggedLine seq)
        : TaggedLine list =
        let order rs =
            rs
            |> List.filter (fun ((v: string), _) -> v <> "" && v.Length >= 4)
            |> List.sortByDescending (fun ((v: string), _) -> v.Length)

        let orderedGlobal = order globalReplacements
        let orderedTrace = order traceOnlyReplacements

        let canonical (line: TaggedLine) =
            // FG-121. Under `options { timestamps() }` the engine's own stamp leads
            // the line, and a replacement VALUE can occur inside it — an env value
            // `2026` rewrote `[2026-08-03T…Z]` into `[${YEAR}-08-03T…Z]`, which the
            // strip below no longer recognised, so the row kept a mangled prefix.
            // Split the stamp off once, canonicalise the BODY only, and put the
            // untouched stamp back so the strip downstream still sees the exact
            // stamp. The split and the folds run on the ANSI-stripped text, the
            // same text `hasTimestampPrefix` and `stripDecoration` read: compared
            // output never carries decoration (`stripDecoration` removes it
            // unconditionally), so nothing is lost, and decoration can no longer
            // hide a stamp from the split or a `+ ` from the xtrace test. A build's
            // own column-zero stamp on an undeclared pipeline is ordinary text and
            // is not split.
            let raw = stripAnsi line.Text

            let stamp, body =
                if stripTimestamps then
                    let body = timestampPrefix.Replace(raw, "")
                    raw.Substring(0, raw.Length - body.Length), body
                else
                    "", raw

            let g =
                orderedGlobal
                |> List.fold (fun (acc: string) (v, token) -> acc.Replace(v, token)) body

            // ENV values rewrite only on xtrace rows — engine-generated lines where
            // expansions actually appear. Ordinary output keeps its literals, which
            // is what makes the replacement injective without parsing the script:
            // `echo /root` compares as text, `+ test -n /root` compares as the
            // expansion it is. (A build printing its own `+ `-shaped line joins the
            // stated mimicry residual; a script that CHANGES PS4 forfeits env
            // canonicalisation on its custom-prefixed rows and any inherited-value
            // difference there diverges VISIBLY — declared, fail-closed.)
            let text =
                if Text.RegularExpressions.Regex.IsMatch(g.TrimStart(), @"^\++ ") then
                    orderedTrace |> List.fold (fun (acc: string) (v, token) -> acc.Replace(v, token)) g
                else
                    g

            { line with Text = stamp + text }

        normaliseOutputInnerTaggedWhen stripTimestamps (lines |> Seq.map canonical)

    let internal normaliseOutputWithInner2
        (stripTimestamps: bool)
        (globalReplacements: (string * string) list)
        (traceOnlyReplacements: (string * string) list)
        (lines: string seq)
        : string list =
        lines
        |> Seq.map (fun line ->
            { Text = line
              HadTimestampPrefix = stripTimestamps && hasTimestampPrefix line })
        |> normaliseOutputWithInnerTagged2 stripTimestamps globalReplacements traceOnlyReplacements
        |> List.map (fun line -> line.Text)

    let normaliseLine (line: string) : string option = normaliseLineWhen false line

    let normaliseOutput (lines: string seq) : string list = normaliseOutputInnerWhen false lines

    /// `applyDurableShape` — the JENKINS side cannot know its own generated
    /// durable-script ids (they exist only inside its container), so its trace
    /// stabilises the full measured shape `@tmp/durable-<8hex>/script.sh` to
    /// `<id>`. The FOGELL side knows its EXACT generated ids and passes them as
    /// ordinary replacements instead, so a fogell-side spoof with a different id
    /// stays literal — a cross-engine spoof pair therefore DIVERGES visibly
    /// rather than collapsing to one token.
    let normaliseOutputShapedWithTimestampCoverage
        (stripTimestamps: bool)
        (applyDurableShape: bool)
        (globalReplacements: (string * string) list)
        (traceOnlyReplacements: (string * string) list)
        (lines: string seq)
        : string list * (int * int) =
        let shaped (l: string) =
            if applyDurableShape then
                Text.RegularExpressions.Regex.Replace(l, "(@tmp/durable-)[0-9a-f]{8}(/script\.sh)", "$1<id>$2")
            else
                l

        let survivors =
            lines
            |> Seq.map (fun line ->
                { Text = shaped line
                  // Capture provenance before any decoration is stripped or
                  // contextual filtering can remove the line. The declaration
                  // gate keeps a user's timestamp-shaped literal ordinary.
                  HadTimestampPrefix = stripTimestamps && hasTimestampPrefix line })
            |> normaliseOutputWithInnerTagged2 stripTimestamps globalReplacements traceOnlyReplacements

        let output = survivors |> List.map (fun line -> line.Text)

        let stamped =
            survivors
            |> List.sumBy (fun line -> if line.HadTimestampPrefix then 1 else 0)

        output, (stamped, List.length survivors)

    let normaliseOutputShaped
        (stripTimestamps: bool)
        (applyDurableShape: bool)
        (globalReplacements: (string * string) list)
        (traceOnlyReplacements: (string * string) list)
        (lines: string seq)
        : string list =
        normaliseOutputShapedWithTimestampCoverage
            stripTimestamps
            applyDurableShape
            globalReplacements
            traceOnlyReplacements
            lines
        |> fst

    let normaliseOutputWith (replacements: (string * string) list) (lines: string seq) : string list =
        normaliseOutputWithInner2 false replacements [] lines


    /// `Terminated` on its own is only a reason when an interrupt was narrated with it;
    /// see [normaliseOutput].
    ///
    /// REVIEW FIX (Codex, PR #16 round 9): moving exception heads and stack frames out of
    /// [isDiagnosticLine] and into a CONTEXTUAL gate left this function unable to see
    /// them. A Jenkins failure explained ONLY by a stack trace would then report NO
    /// reason while Fogell's `ERROR:` reported one — a DiagnosticSilence divergence on an
    /// otherwise matching run. The same context detection has to serve both.
    let reportedFailureReasonWhen (stripTimestamps: bool) (lines: string seq) : bool =
        let all = lines |> Seq.toArray

        let clean (l: string) = (stripDecoration stripTimestamps l).Trim()

        let cleaned = all |> Array.map clean

        let hasStackTrace =
            cleaned |> Array.mapi (fun i _ -> startsStackTrace cleaned i) |> Array.exists id

        hasStackTrace
        || (all
            |> Array.map clean
            |> Array.exists (fun l -> isDiagnosticLine l || isTimeoutNarration l))

    /// The exclusions above are part of the contract, so they are published with
    /// every receipt rather than buried in code.
    let comparisonContract =
        [ "compared: terminal result"
          // "ordered" IS NOT UNCONDITIONAL. A CONCURRENT case compares output as a
          // MULTISET, because branch interleaving is not a difference between the
          // engines — and this line said "ordered" with nothing anywhere in the
          // receipt to say otherwise. FG-151. The per-receipt disclosure is emitted
          // by `Compare.compareOutput`; this states the rule it belongs to.
          "compared: ordered normalised output lines — EXCEPT concurrent (parallel) cases,"
          "  which compare as a MULTISET; those receipts say so under `## Output comparison notes`"
          "compared: canonical workspace hash over sorted (path, content-hash) pairs"
          "excluded: timestamps() PREFIX TEXT, ANSI escapes, blank lines"
          "compared as a CLASSIFICATION — none / partial / all — over COMPARED survivors:"
          "  timestamps() coverage. Two engines read two clocks, so the instants can never"
          "  agree; an engine that IGNORED the option, or honoured it for only some lines,"
          "  would otherwise compare equal to one that honoured it fully. Line COUNTS are"
          "  printed but never compared — the engines do not emit the same number of lines."
          "  FG-118: prefix provenance follows each line through the complete contextual"
          "  normaliser, and both counts come from its final survivor set. `all` means every"
          "  compared survivor carried a prefix; suppressed narration and other raw console"
          "  lines are outside both output comparison and this coverage claim."
          "  FG-053: this exclusion was CLAIMED and unimplemented until a case first used"
          "  the option, and read as true only because none did."
          "excluded: [Pipeline] graph annotations, 'Post stage' label, node/workspace banners, Started/Finished lines"
          "compared: shell xtrace ('+ cmd') lines — BOTH engines run `sh -xe`, so the"
          "  trace is identical emitted output, continuations included (retires FG-002c)"
          "compared: xtrace CONTINUATION rows — dash traces a multiline word with `+ ` on"
          "  its first physical line only (measured; no re-quoting, no record terminator),"
          "  so a mismatching line pair that becomes equal under the SAME inherited-env"
          "  replacement list applied to BOTH sides compares as that canonical form."
          "  Literals cancel; receipt lines stay literal; the rule can only turn a"
          "  divergence into an equality, never hide one direction only. EVERY pair the"
          "  rule folds is LISTED in the receipt that used it (sealed) — EXCEPT in a"
          "  CONCURRENT case, where multiset comparison pairs nothing, so the receipt"
          "  lists PER-SIDE OCCURRENCES (`jenkins ${HOME}`, `fogell ${HOME}`) and their"
          "  multiplicity instead of pair records. FG-158: this said EVERY pair while the"
          "  concurrent path emitted occurrences, which is a promise the mode cannot keep."
          "  A canonical"
          "  comparison is always visible in the case it decided — ordinary output that"
          "  prints an inherited value (e.g. `printenv HOME`) folds the same way, the"
          "  declared environment-of-necessity class the ${WORKSPACE} fold already is"
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
          "excluded: a Java exception head, the message lines it spans (at most 8, FG-243)"
          "  and the `at …(…)` frames under it — ONLY when a frame confirms the head (the"
          "  next line, or two consecutive JVM-shaped frames after the message lines), so"
          "  a build echoing an exception name keeps it; the confirmed trace counts as the"
          "  reported reason; a remoting `hudson.remoting.ProxyException:` wrapper on the"
          "  ErrorAction marker is the controller's, not the build's, and is excluded too"
          "not compared: wall-clock duration, log ordering across stdout/stderr, diagnostic wording" ]
    /// Back-compat for callers with no script in hand (tests, ad-hoc tools).
    /// A trace built through the differential always passes the script's answer.
    let reportedFailureReason (lines: string seq) : bool = reportedFailureReasonWhen false lines
