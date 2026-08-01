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
        let script = File.ReadAllText jenkinsfile

        let digest =
            use h = Security.Cryptography.SHA256.Create()

            Text.Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n"))
            |> h.ComputeHash
            |> Convert.ToHexString
            |> fun x -> x.ToLowerInvariant()

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

        if not (List.isEmpty plan.NeedsReconciliation) then
            let named =
                plan.NeedsReconciliation
                |> List.map (fun (st, i) -> $"{st}#{i}")
                |> String.concat ", "

            eprintfn $"needs-reconciliation: {named} — a started step has no recorded outcome; refusing to guess"
            3
        else

        let resuming = not (Map.isEmpty plan.Steps)
        // the first attempt starts clean; a RESUME must keep the workspace —
        // that is the state the finished steps produced
        let freshWorkspace = not resuming

        if resuming then
            printfn "resuming: one recovery event for this build"

        use journal = Journal.openAt journalPath EveryStep

        // first attempt records what definition this journal belongs to
        if plan.ScriptDigest.IsNone then
            journal.Append(ScriptDigest digest)
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

        match FogellSide.runPersisted [] workspaceRoot jobName freshWorkspace hooks script with
        | Result.Error e ->
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
