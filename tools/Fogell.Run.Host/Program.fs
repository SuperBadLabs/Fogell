module Fogell.Run.Host.Program

open System
open System.IO
open Fogell.Domain
open Fogell.Differential
open Fogell.Journal

/// FG-112. The restart lane's HOST: runs a real Jenkinsfile through the real
/// walker with the FG-025 journal wired in, as a separate killable process —
/// the acceptance criterion is a genuine SIGKILL, and a process cannot kill
/// itself and then observe its own recovery.
///
/// Semantics on re-invocation over the same journal:
///  * a durably finished step is skipped silently (exactly-once);
///  * a step that STARTED without finishing is surfaced and the run REFUSES
///    (exit 3, steps named) — the engine genuinely does not know whether the
///    effect landed, and re-running is the at-least-once semantics ADR 0003
///    rejects;
///  * a terminal journal makes the run a no-op ("already-terminal", exit 0).
///
/// usage: fogell-run-host <jenkinsfile> <workspace-root> <job-name> <journal>
[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | [ jenkinsfile; workspaceRoot; jobName; journalArg ] ->
        // a relative journal path would hand Journal.ensure an empty directory
        // name and fail the first append — normalise before anything reads it
        let journalPath = Path.GetFullPath journalArg

        // a journal INSIDE the workspace would be unlinked by the fresh-attempt
        // wipe — every record then lands on an unlinked inode and resume reads
        // an empty file. Refused by name; the journal is controller-side state.
        // trailing separators survive GetFullPath and would turn the prefix
        // into "…//", defeating the containment check
        let workspaceFull =
            Path
                .GetFullPath(Path.Combine(workspaceRoot, jobName))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

        if journalPath.StartsWith(workspaceFull + string Path.DirectorySeparatorChar) then
            eprintfn $"journal path is inside the workspace ({workspaceFull}) — the fresh-attempt wipe would unlink it; keep it controller-side"
            exit 2

        let script = File.ReadAllText jenkinsfile

        let digest =
            use h = Security.Cryptography.SHA256.Create()

            Text.Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n"))
            |> h.ComputeHash
            |> Convert.ToHexString
            |> fun x -> x.ToLowerInvariant()

        // control characters in the identity would tear the journal's wire
        // format exactly like a hostile stage name — refuse by name
        if
            [ workspaceRoot; jobName ]
            |> List.exists (fun v -> v.Contains '\t' || v.Contains '\n' || v.Contains '\r')
        then
            eprintfn "workspace-root/job-name contain tab/newline/carriage-return — unjournalable; refusing"
            exit 2

        // repair a torn tail BEFORE the plan is built: the reconciliation
        // refusal exits before any journal open, and an operator's appended
        // fix would otherwise land invisibly behind the fragment
        Journal.repairTail journalPath

        let plan = Resume.plan (Journal.read journalPath)

        match plan.Terminal with
        | Some t ->
            printfn $"already-terminal: {BuildStatus.toWireString t}"
            0
        | None ->

        // A resume against a CHANGED definition hybrid-executes two pipelines
        // over one (stage, index) key space — a changed step occupying a
        // finished key would be silently skipped. Refuse by name instead.
        match plan.ScriptDigest with
        | Some recorded when recorded <> digest ->
            eprintfn "definition-changed: the journal belongs to a different Jenkinsfile; refusing to resume a hybrid"
            4
        | _ ->

        // same shape for the WORKSPACE: durable setup steps were skipped on the
        // strength of effects that live in a particular tree
        let rootFull = Path.GetFullPath workspaceRoot

        match plan.WorkspaceIdentity with
        | Some(r, j) when r <> rootFull || j <> jobName ->
            eprintfn $"workspace-changed: the journal belongs to ({r}, {j}); refusing to resume against ({rootFull}, {jobName})"
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
            if Directory.Exists workspaceFull then
                Directory.Delete(workspaceFull, true)

            Directory.CreateDirectory workspaceFull |> ignore
            // mirror runWith's fresh path: a new job has no SCM build history
            WalkerGit.resetHistory (Path.Combine(workspaceRoot, "_artifacts")) jobName

        if resuming then
            printfn "resuming: one recovery event for this build"

        use journal = Journal.openAt journalPath EveryStep

        // first attempt records what definition this journal belongs to
        // backfilled INDEPENDENTLY: a death between the two appends must not
        // leave the missing one unrecordable forever
        if plan.ScriptDigest.IsNone then
            journal.Append(ScriptDigest digest)

        if plan.WorkspaceIdentity.IsNone then
            journal.Append(WorkspaceIdentity(rootFull, jobName))

        if plan.ScriptDigest.IsNone || plan.WorkspaceIdentity.IsNone then
            journal.Sync()

        let hooks =
            { IsRestartedRun = resuming
              SkippedStatus =
                fun stage i ->
                    match Resume.dispositionOf plan stage i with
                    | AlreadyFinished st -> Some st
                    | _ -> None
              ShouldExecute =
                fun stage i ->
                    let run = Resume.shouldExecute plan stage i

                    if not run then
                        printfn $"skip (durably finished): {stage}#{i}"

                    run
              OnStepStarted =
                fun stage i name ->
                    journal.Append(StepStarted(stage, i, name))
                    journal.Sync()
              OnStepFinished = fun stage i status -> journal.Append(StepFinished(stage, i, status))
              OnStageCommitted =
                fun stage ->
                    journal.Append(StageCommitted stage)
                    journal.Sync() }

        // the workspace is already prepared above — never re-wipe here
        match FogellSide.runPersisted [] workspaceRoot jobName false hooks script with
        | Result.Error e ->
            // The attempt is OVER and failed — steps may already be durably
            // finished (the leak guard, for one, refuses AFTER they ran), and
            // leaving no terminal record would let a later invocation resume
            // into a finished run. Terminal failure is the honest state.
            journal.Append(BuildFinished BuildStatus.Failure)
            journal.Close()
            eprintfn $"run failed: {e}"
            2
        | Ok trace ->
            let status =
                BuildStatus.ofWireString trace.Result |> Option.defaultValue BuildStatus.Failure

            journal.Append(BuildFinished status)
            journal.Close()
            printfn $"completed: {trace.Result}"
            for l in trace.Output do printfn "| %s" l
            if trace.Result = "success" then 0 else 1
    | _ ->
        eprintfn "usage: fogell-run-host <jenkinsfile> <workspace-root> <job-name> <journal>"
        2
