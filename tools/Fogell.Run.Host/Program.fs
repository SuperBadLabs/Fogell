module Fogell.Run.Host.Program

open System
open System.IO
open System.Text
open System.Threading
open Fogell.Domain
open Fogell.Runtime
open Fogell.Journal

[<EntryPoint>]
let main argv =
    let controllerSupervised =
        match Environment.GetEnvironmentVariable "FOGELL_CONTROLLER_LIVENESS_PIPE" with
        | null
        | "" -> false
        | "1" when OperatingSystem.IsLinux() ->
            // PDEATHSIG follows the particular native thread that forked this
            // process, not the controller process lifetime. A controller-owned pipe
            // is process-scoped instead: every exit shape closes its writer. Run the
            // blocking read on a dedicated background thread; any byte is a protocol
            // violation, and EOF terminates Run.Host so its own step-watchdog pipes
            // close and reap nested groups.
            let monitor =
                Thread(
                    ThreadStart(fun () ->
                        let refuse reason =
                            eprintfn "controller liveness supervision refused: %s" reason
                            Environment.Exit 70

                        try
                            use input = Console.OpenStandardInput()
                            let probe = Array.zeroCreate<byte> 1
                            let read = input.Read(probe, 0, probe.Length)

                            if read = 0 then
                                refuse "controller liveness pipe closed"
                            else
                                refuse "controller liveness pipe carried unexpected data"
                        with error ->
                            refuse $"controller liveness pipe failed: {error.GetType().Name}"))

            monitor.IsBackground <- true
            monitor.Name <- "fogell-controller-liveness"
            monitor.Start()
            true
        | "1" ->
            eprintfn "controller liveness-pipe supervision is supported only on Linux"
            exit 2
        | _ ->
            eprintfn "FOGELL_CONTROLLER_LIVENESS_PIPE must be exactly 1 when set"
            exit 2

    // Refuse malformed operator policy before creating durable run state.
    match Fogell.Execution.ArtifactPolicy.loadEnvironment () with
    | Ok _ -> ()
    | Error reason ->
        eprintfn "%s" reason
        exit 2

    let eventPath =
        match Environment.GetEnvironmentVariable "FOGELL_EVENT_FILE" with
        | null
        | "" -> None
        | value -> Some(Path.GetFullPath value)

    let buildNumber =
        match Environment.GetEnvironmentVariable "FOGELL_BUILD_NUMBER" with
        | null
        | "" ->
            // Standalone restart/proof callers predate the controller claim
            // boundary and retain their historical single-build contract.
            1
        | raw ->
            match Int32.TryParse raw with
            | true, value when value > 0 -> value
            | _ ->
                eprintfn "FOGELL_BUILD_NUMBER must be a positive integer"
                exit 2

    let eventGate = obj()

    // The event file is an IPC stream, not a second unbounded build log.  A
    // user process may write an arbitrarily long newline-free record, so one
    // logical output value is represented by one or more consecutive frames.
    // Capping UTF-16 input at 16 Ki code units caps every encoded frame below
    // 64 KiB (UTF-8 needs at most three bytes for one unpaired code unit and
    // four for a surrogate pair).  Keep a valid surrogate pair together at a
    // boundary.  eventGate covers the WHOLE logical value, so another emitter
    // cannot interleave its frames between these ones.
    let maxEventFrameBytes = 64 * 1024
    let maxEventFrameChars = maxEventFrameBytes / 4

    let appendEventFrames (path: string) (body: string) =
        let value = if isNull body then "" else body

        let append (text: string) =
            let bytes = Encoding.UTF8.GetBytes text

            if bytes.Length > maxEventFrameBytes then
                invalidOp "event frame exceeded its UTF-8 byte bound"

            File.AppendAllText(path, Convert.ToBase64String(bytes) + "\n")

        if value.Length = 0 then
            append ""
        else
            let mutable start = 0

            while start < value.Length do
                let mutable count = min maxEventFrameChars (value.Length - start)

                if
                    start + count < value.Length
                    && Char.IsHighSurrogate value[start + count - 1]
                    && Char.IsLowSurrogate value[start + count]
                then
                    count <- count - 1

                append (value.Substring(start, count))
                start <- start + count

    let emitEvent (body: string) =
        try
            if eventPath.IsNone then Console.WriteLine body
            eventPath
            |> Option.iter (fun path ->
                lock eventGate (fun () ->
                    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                    appendEventFrames path body))
        with
        | :? OutputPublicationException -> reraise ()
        | ex ->
            raise (
                OutputPublicationException(
                    "controller event publication failed",
                    ex))

    let emitDiagnostic diagnostic =
        try
            if eventPath.IsNone then Console.Error.WriteLine(ExecutionDiagnostic.serialize diagnostic)
            eventPath |> Option.iter (fun path ->
                lock eventGate (fun () ->
                    let json = ExecutionDiagnostic.serialize diagnostic
                    let bytes = Encoding.UTF8.GetBytes json
                    if bytes.Length > 16384 then invalidOp "diagnostic exceeded frame bound"
                    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                    File.AppendAllText(path, "D1:" + Convert.ToBase64String bytes + "\n")))
        with ex -> raise (OutputPublicationException("controller diagnostic publication failed", ex))

    match Array.toList argv with
    | [ pipelineFile; workspaceRoot; jobName; journalArg ] ->
        // Paths first, RESOLVED once: every containment decision below compares
        // physical locations, because a symlink anywhere in the chain makes a
        // lexical prefix meaningless (the wipe follows links, the OS does not
        // care about our string arithmetic).
        //
        // EVERY existing component is resolved, walking from the root — an
        // intermediate link is invisible if only the deepest component is
        // examined and that component is not itself a link.
        let resolve (p: string) =
            // A real realpath: after substituting a link target the path is
            // DIFFERENT and its own components may be links, so resolution
            // restarts from the beginning. (Resolving each component once left
            // `/safe/link -> /safe/hop/sub` with `/safe/hop -> /srv` pointing
            // at a location the kernel would never use.) LinkTarget rather than
            // ResolveLinkTarget so DANGLING links are followed too; bounded, so
            // a cycle refuses instead of spinning.
            let mutable current = Path.GetFullPath p
            let mutable rounds = 0
            let mutable settled = false

            while not settled && rounds < 64 do
                rounds <- rounds + 1
                let parts = current.Split([| Path.DirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
                let mutable acc = string Path.DirectorySeparatorChar
                let mutable i = 0
                let mutable substituted = false

                while not substituted && i < parts.Length do
                    let candidate = Path.Combine(acc, parts[i])

                    match (FileInfo candidate).LinkTarget with
                    | null ->
                        acc <- candidate
                        i <- i + 1
                    | target ->
                        let resolvedHead =
                            if Path.IsPathRooted target then
                                Path.GetFullPath target
                            else
                                Path.GetFullPath(Path.Combine(acc, target))

                        let rest = parts[i + 1 ..] |> String.concat (string Path.DirectorySeparatorChar)
                        current <- if rest = "" then resolvedHead else Path.Combine(resolvedHead, rest)
                        substituted <- true

                if not substituted then
                    current <- acc
                    settled <- true

            if not settled then
                eprintfn $"symlink resolution did not settle for {p} (cycle?) — refusing"
                exit 2

            current

        let trimSep (p: string) =
            p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

        // Identity components are length-prefixed: a resolved path may contain
        // '|', and plain concatenation made distinct tuples compare equal.
        // The LEXICAL root joins the resolved triple: the walker exposes the
        // raw root to the build (WORKSPACE and friends derive from it), so two
        // spellings that resolve to one tree are still different builds.
        let encodeIdentity (lexical: string) (r: string) (w: string) (a: string) =
            $"{lexical.Length}:{lexical}|{r.Length}:{r}|{w.Length}:{w}|{a.Length}:{a}"

        // a relative journal path would hand Journal.ensure an empty directory
        // name and fail the first append — normalise before anything reads it
        let journalPath = Path.GetFullPath journalArg

        // The job name is also a controller-state KEY (artifacts, stashes, SCM
        // records all combine it as a relative segment), so it must be a plain
        // relative name — not rooted, not traversing.
        if Path.IsPathRooted jobName || jobName.Split([| '/'; '\\' |]) |> Array.contains ".." then
            eprintfn $"job-name must be a plain relative name (not rooted, no traversal): {jobName}"
            exit 2

        let workspaceFull = trimSep (Path.GetFullPath(Path.Combine(workspaceRoot, jobName)))
        let realWorkspace = trimSep (resolve workspaceFull)
        let realRoot = trimSep (resolve workspaceRoot)

        // the ARTIFACT root too: stashes, archived artifacts and SCM records
        // live under it, and it can be retargeted independently of the
        // workspace (a symlink of its own)
        let artifactsResolved = trimSep (resolve (Path.Combine(workspaceRoot, "_artifacts")))

        // Controller-side state must NOT live inside the workspace: stashes and
        // SCM records live under _artifacts, credential material under
        // _secrets, and the fresh-attempt wipe would destroy state a resume is
        // entitled to (or another job's secrets). EVERY controller root is
        // checked, both directions — a job named after one, or a symlinked
        // store pointing into the job tree.
        let controllerRoots =
            [ "_artifacts", artifactsResolved
              "_secrets", trimSep (resolve (Path.Combine(workspaceRoot, "_secrets"))) ]

        for name, path in controllerRoots do
            if
                path = realWorkspace
                || path.StartsWith(realWorkspace + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || realWorkspace.StartsWith(path + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
            then
                eprintfn $"the {name} store ({path}) overlaps the workspace ({realWorkspace}) — the wipe would destroy controller-side state; refusing"
                exit 2

        // ORDINAL: the default StartsWith is culture-sensitive and can treat
        // ignorable characters (a soft hyphen, say) as absent, authorising a
        // sibling directory that is not actually beneath the root.
        if not (realWorkspace.StartsWith(realRoot + string Path.DirectorySeparatorChar, StringComparison.Ordinal)) then
            eprintfn $"job-name resolves outside the workspace root ({realWorkspace} vs {realRoot}) — the wipe would delete an unrelated directory; refusing"
            exit 2

        // a journal PHYSICALLY inside the workspace would be unlinked by the
        // fresh-attempt wipe — every record then lands on an unlinked inode and
        // resume reads an empty file. Compared resolved: an aliasing symlink
        // (root=/tmp/link -> /tmp/real, journal under /tmp/real/job) is the
        // same physical location and must refuse too.
        // BOTH forms: the resolved check catches an aliasing root, the lexical
        // one catches a journal that IS a symlink sitting inside the workspace
        // (its target is controller-side, but the wipe removes the link itself)
        if
            (resolve journalPath).StartsWith(realWorkspace + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || journalPath.StartsWith(workspaceFull + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
        then
            eprintfn $"journal path is inside the workspace ({realWorkspace}) — the fresh-attempt wipe would unlink it; keep it controller-side"
            exit 2

        match eventPath with
        | Some path when
            (resolve path).StartsWith(realWorkspace + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || path.StartsWith(workspaceFull + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ->
            eprintfn $"event file is inside the workspace ({realWorkspace}) — keep controller protocol state outside build control"
            exit 2
        | _ -> ()

        // control characters in the identity would tear the journal's wire
        // format exactly like a hostile stage name — refuse by name
        // the RESOLVED values are what get journaled: a clean lexical root can
        // resolve through a symlink into a target carrying a delimiter
        if
            [ workspaceRoot; jobName; realWorkspace; realRoot; artifactsResolved ]
            |> List.exists (fun v -> v.Contains '\t' || v.Contains '\n' || v.Contains '\r')
        then
            eprintfn "workspace path or job-name contains tab/newline/carriage-return (after symlink resolution) — unjournalable; refusing"
            exit 2

        // FG-253. An unsupported agent must be refused before even the recovery
        // write below. Read only enough journal state to preserve the established
        // terminal no-op: a complete, newline-terminated BuildFinished record
        // means the pipeline definition may already have been rotated and must not be
        // required. For every non-terminal journal, including one with a torn
        // tail, run the same persisted preflight while the journal bytes are
        // still untouched.
        let readAndPreflightScript () =
            let candidate = File.ReadAllText pipelineFile

            match Runtime.preflight candidate with
            | Error why ->
                eprintfn $"{why}"
                exit 2
            | Ok _ -> candidate

        let terminalPlan, scriptBeforeRepair =
            if not (File.Exists journalPath) then
                None, Some(readAndPreflightScript ())
            else
                // Bind framing and decoded records to one immutable byte
                // snapshot. FileShare is only advisory on the supported Linux
                // runtime, so neither a lock flag nor two pathname reads can
                // prevent an old newline from being associated with a newly
                // appended, unterminated BuildFinished record. Decode only the
                // newline-terminated prefix: a durable BuildFinished remains a
                // terminal no-op even if unrelated torn bytes follow it, while
                // an unterminated BuildFinished is never trusted.
                let snapshotBytes = File.ReadAllBytes journalPath

                let durableLength =
                    snapshotBytes
                    |> Array.tryFindIndexBack ((=) (byte '\n'))
                    |> Option.map ((+) 1)
                    |> Option.defaultValue 0

                let durableRecords =
                    use snapshot = new MemoryStream(snapshotBytes, 0, durableLength, false)
                    use reader = new StreamReader(snapshot, Encoding.UTF8, true)
                    let decoded = ResizeArray<Record>()
                    let mutable keepReading = true

                    while keepReading && not reader.EndOfStream do
                        match Record.decode (reader.ReadLine()) with
                        | Some record -> decoded.Add record
                        | None -> keepReading <- false

                    decoded |> Seq.toList

                let hasDurableTerminal =
                    durableRecords
                    |> List.exists (function
                        | BuildFinished _ -> true
                        | _ -> false)

                if hasDurableTerminal then
                    Some(Resume.plan durableRecords), None
                else
                    None, Some(readAndPreflightScript ())

        // Repair a torn tail BEFORE the non-terminal plan is built: an
        // operator's appended fix would otherwise land invisibly behind the
        // fragment. The unsupported-agent refusal above exits before this
        // mutation; Journal.openAt repeats the repair before its first append as
        // the deepest durability guard. A durable terminal plan is carried from
        // the locked snapshot and needs neither a repair nor a second read.
        let plan =
            match terminalPlan with
            | Some completed -> completed
            | None ->
                Journal.repairTail journalPath
                Resume.plan (Journal.read journalPath)

        match plan.Terminal with
        | Some t ->
            printfn $"already-terminal: {BuildStatus.toWireString t}"
            0
        | None ->

        // Populated by the read-only pre-repair guard on every non-terminal
        // journal. Only a parsed BuildFinished record whose line is durably
        // terminated can leave it empty, and that record survives repairTail.
        let script =
            match scriptBeforeRepair with
            | Some candidate -> candidate
            | None -> readAndPreflightScript ()

        let digest =
            use h = Security.Cryptography.SHA256.Create()

            Text.Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n"))
            |> h.ComputeHash
            |> Convert.ToHexString
            |> fun x -> x.ToLowerInvariant()

        // A resume against a CHANGED definition hybrid-executes two pipelines
        // over one (stage, index) key space — a changed step occupying a
        // finished key would be silently skipped. Refuse by name instead.
        match plan.ScriptDigest with
        | Some recorded when recorded <> digest ->
            eprintfn "definition-changed: the journal belongs to a different pipeline definition; refusing to resume a hybrid"
            4
        | _ ->

        // same shape for the WORKSPACE: durable setup steps were skipped on the
        // strength of effects that live in a particular tree
        // RESOLVED root: a symlink retargeted between attempts changes the
        // physical tree while the lexical path is unchanged, and the resumed
        // run would skip durable setup against a different directory.
        // Keyed on the resolved WORKSPACE, not just the root: a symlink inside
        // the job path retargeted between attempts (ws/job -> ws/a becoming
        // ws/job -> ws/b) keeps root and lexical name equal while the physical
        // tree changes underneath the durable steps.
        // Root AND workspace: two different roots can reach one physical job
        // directory through symlinks while the walker derives controller state
        // (artifacts, stashes, SCM records) from the ROOT — which would differ.
        // Compared against what the PREVIOUS attempt recorded; the value this
        // attempt records is computed after the wipe (below), because the wipe
        // can replace a workspace SYMLINK with a real directory and change the
        // physical target under us.
        // LENGTH-PREFIXED: a resolved path may legitimately contain '|', and
        // plain concatenation let distinct tuples collide into one string.
        let identity = encodeIdentity (trimSep (Path.GetFullPath workspaceRoot)) realRoot realWorkspace artifactsResolved

        match plan.WorkspaceIdentity with
        | Some(w, j) when w <> identity || j <> jobName ->
            eprintfn $"workspace-changed: the journal belongs to ({w}, {j}); refusing to resume against ({identity}, {jobName})"
            4
        | _ ->

        if not (List.isEmpty plan.NeedsReconciliation) then
            let named =
                plan.NeedsReconciliation
                |> List.map (fun (st, i) -> $"{st}#{i}")
                |> String.concat ", "

            eprintfn $"needs-reconciliation: {named} — a started step has no recorded outcome; refusing to guess"
            3
        else

        // a digest-only journal (died after the first sync, before any step)
        // is STILL a second attempt: the digest is written by an attempt, so
        // its presence in the plan proves one existed — without this, the
        // workspace of a real prior attempt is wiped and isRestartedRun lies
        let resuming = plan.ScriptDigest.IsSome || not (Map.isEmpty plan.Steps)

        // The HOST owns the fresh-attempt wipe, and it happens BEFORE the first
        // metadata append: a kill between metadata and a later wipe would make
        // the next invocation "resume" over a never-wiped stale tree. With this
        // order, metadata-present always implies workspace-prepared — and a
        // kill after the wipe but before metadata just wipes again, idempotent.
        if not resuming then
            // Delete the path that was VALIDATED, physically: resolving and then
            // deleting the lexical path is a check/use race — a component
            // swapped between the two would redirect the recursive delete. The
            // final component must not be a link either, or the wipe would
            // follow it rather than remove the workspace.
            if not (isNull (FileInfo workspaceFull).LinkTarget) then
                eprintfn $"workspace path {workspaceFull} is a symlink — refusing to wipe through it"
                exit 2

            if Directory.Exists realWorkspace then
                Directory.Delete(realWorkspace, true)

            Directory.CreateDirectory workspaceFull |> ignore
            // FG-267. Source is installed only after the host's fresh workspace
            // wipe and before journal metadata or any pipeline step. Resume
            // retains the existing execution workspace and never rematerializes.
            match Environment.GetEnvironmentVariable "FOGELL_SOURCE_SNAPSHOT_FILE" with
            | null | "" -> ()
            | snapshotPath ->
                let info = FileInfo snapshotPath
                if info.Length > int64 Fogell.Domain.SourceSnapshot.MaxEnvelopeBytes then
                    eprintfn "source snapshot exceeds transport limit"
                    exit 2
                let bytes = File.ReadAllBytes snapshotPath
                match Fogell.Domain.SourceSnapshot.decode bytes with
                | Ok(Some snapshot) ->
                    if snapshot.PipelineSha256 <> Fogell.Domain.SourceSnapshot.digest (File.ReadAllBytes pipelineFile) then
                        eprintfn "source snapshot pipeline identity differs"
                        exit 2
                    match Fogell.Domain.SourceSnapshot.materialize snapshot realWorkspace with
                    | Ok () -> ()
                    | Error _ ->
                        eprintfn "source snapshot materialization refused"
                        exit 2
                | _ ->
                    eprintfn "invalid source snapshot transport"
                    exit 2
            // mirror runWith's fresh path: a new job has no SCM build history


        if resuming then
            printfn "resuming: one recovery event for this build"

        use journal = Journal.openAt journalPath EveryStep

        // first attempt records what definition this journal belongs to
        // backfilled INDEPENDENTLY: a death between the two appends must not
        // leave the missing one unrecordable forever
        if plan.ScriptDigest.IsNone then
            journal.Append(ScriptDigest digest)

        if plan.WorkspaceIdentity.IsNone then
            // recomputed post-wipe: the fresh path may have replaced a symlink
            // with a real directory, making the pre-wipe resolution stale
            let identityNow =
                encodeIdentity
                    (trimSep (Path.GetFullPath workspaceRoot))
                    (trimSep (resolve workspaceRoot))
                    (trimSep (resolve workspaceFull))
                    (trimSep (resolve (Path.Combine(workspaceRoot, "_artifacts"))))
            journal.Append(WorkspaceIdentity(identityNow, jobName))

        if plan.ScriptDigest.IsNone || plan.WorkspaceIdentity.IsNone then
            journal.Sync()

        let hooks =
            { OnDiagnostic = emitDiagnostic
              OnOutput = emitEvent
              SkippedStatus = fun stage i ->
                  match Resume.dispositionOf plan stage i with
                  | AlreadyFinished status -> Some status
                  | _ -> None
              OnStepStarted = fun stage i name ->
                  journal.Append(StepStarted(stage, i, name))
                  journal.Sync()
                  emitEvent $"step-started: {stage}#{i} {name}"
              OnStepFinished = fun stage i status reason ->
                  journal.AppendStepFinished(stage, i, status, reason)
                  emitEvent $"step-finished: {stage}#{i} {BuildStatus.toWireString status}"
              OnStageCommitted = fun stage ->
                  journal.Append(StageCommitted stage)
                  journal.Sync()
                  emitEvent $"stage-committed: {stage}" }

        let persistedRun =
            try
                let result = Runtime.runPersisted workspaceRoot jobName buildNumber hooks script

                Choice1Of2 result
            with :? OutputPublicationException as failure ->
                Choice2Of2 failure

        match persistedRun with
        | Choice2Of2 _ ->
            // Infrastructure could not preserve the progressive event stream.
            // Do not turn that into a semantic build failure: without a terminal
            // journal record the controller's natural-exit path requires
            // reconciliation and cannot publish guessed build truth.
            journal.Close()
            eprintfn "controller event publication failed; reconciliation required"
            3
        | Choice1Of2(Result.Error _) ->
            // The attempt is OVER and failed — steps may already be durably
            // finished (the leak guard, for one, refuses AFTER they ran), and
            // leaving no terminal record would let a later invocation resume
            // into a finished run. Terminal failure is the honest state.
            // Never forward e here: it may contain credentials or unbounded
            // user input. The persisted runner publishes classified exceptions;
            // this fixed fallback also covers returned engine refusals. Publish
            // before the terminal journal record so missing evidence cannot be
            // mistaken for an ordinary completed failure.
            emitDiagnostic (ExecutionDiagnostic.create "infrastructure" "failure" "RUN_FAILED: runner could not complete the build")
            emitEvent "runner-failure: RUN_FAILED: runner could not complete the build"
            journal.Append(BuildFinished BuildStatus.Failure)
            journal.Close()
            eprintfn "runner-failure: RUN_FAILED: runner could not complete the build"
            2
        | Choice1Of2(Ok result) ->
            let status =
                BuildStatus.ofWireString result.Result |> Option.defaultValue BuildStatus.Failure

            journal.Append(BuildFinished status)
            journal.Close()
            printfn $"completed: {result.Result}"
            if result.Result = "success" then 0 else 1
    | _ ->
        eprintfn "usage: fogell-run-host <pipelineFile> <workspace-root> <job-name> <journal>"
        2
