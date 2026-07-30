module Fogell.Crash.Harness.Program

open System
open System.IO
open Fogell.Domain
open Fogell.Journal

/// A minimal journalled pipeline runner, used ONLY to prove FG-025 with a real
/// SIGKILL. It runs three steps in one stage; each step appends a line to a
/// marker file, so a step that executes twice is visible as a duplicate.
///
/// Existing as a separate process is the point: the acceptance criterion is a
/// genuine crash, and you cannot SIGKILL yourself and then observe the recovery.
///
/// usage: fogell-crash-harness <journal> <marker> <policy: step|stage> [--die-at N]
[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | journalPath :: markerPath :: policyName :: rest ->
        let policy =
            match policyName with
            | "step" -> EveryStep
            | "stage" -> EveryStage
            | _ -> EveryStep

        let dieAt =
            rest
            |> List.pairwise
            |> List.tryPick (fun (a, b) ->
                if a = "--die-at" then
                    match Int32.TryParse b with
                    | true, n -> Some n
                    | _ -> None
                else
                    None)

        let plan = Resume.plan (Journal.read journalPath)

        if not (Resume.isResumable plan) then
            printfn "already-terminal"
            0
        else

        use journal = Journal.openAt journalPath policy
        let stage = "Build"
        let mutable status = BuildStatus.Success

        for i in 0..2 do
            if Resume.shouldExecute plan stage i then
                journal.Append(StepStarted(stage, i, $"step-{i}"))

                // die AFTER the started record is durable but BEFORE finishing —
                // the hardest case, because the effect may or may not have landed
                if dieAt = Some i then
                    journal.Sync()
                    printfn $"dying at step {i}"
                    Console.Out.Flush()
                    // SIGKILL ourselves: no finalizers, no flush, a real crash
                    Diagnostics.Process.GetCurrentProcess().Kill()
                    Threading.Thread.Sleep 5_000

                // the observable effect: one line per execution
                File.AppendAllText(markerPath, $"executed step-{i}\n")
                journal.Append(StepFinished(stage, i, BuildStatus.Success))
            else
                printfn $"skipping step {i}: {Resume.dispositionOf plan stage i}"

        journal.Append(StageCommitted stage)
        journal.Append(BuildFinished status)
        journal.Close()
        printfn "completed"
        0
    | _ ->
        eprintfn "usage: fogell-crash-harness <journal> <marker> <step|stage> [--die-at N]"
        2
