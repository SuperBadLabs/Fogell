namespace Fogell.Differential

open System.IO
open Fogell.Domain
open Fogell.Execution
open Fogell.Ir

/// The Fogell side. Parses the same Jenkinsfile, walks its stages, executes each
/// step, and reduces the run to a [Trace] in the same canonical form.
module FogellSide =

    /// Walk a parsed declarative pipeline. This is the minimum sequencer needed
    /// to make the differential meaningful; the durable scheduler (Wave 2) is a
    /// separate concern and is not on this path.
    let run (workspaceRoot: string) (jobName: string) (script: string) : Result<Trace, string> =
        match Fogell.Pipeline.Parser.Parser.parse script with
        | Result.Error e -> Result.Error $"{Fogell.Admission.ErrorCode.toWireString e.Code} at {e.Position}: {e.Message}"
        | Result.Ok pipeline ->
            let output = System.Collections.Generic.List<string>()
            let workspace = Path.Combine(workspaceRoot, jobName)
            if Directory.Exists workspace then Directory.Delete(workspace, true)
            Directory.CreateDirectory workspace |> ignore

            /// Environment visible to a step: pipeline scope, overridden by stage
            /// scope. Lexical, and stage wins — the semantics measured on Jenkins.
            let envFor (stage: Stage) =
                (pipeline.Environment @ stage.Environment)
                |> List.fold (fun acc (k, v) -> Map.add k v acc) Map.empty
                |> Map.toList

            let mutable status = BuildStatus.Success

            let runStep (stage: Stage) (cwd: string) (step: Step) =
                let script =
                    match step.Positional with
                    | s :: _ -> Some s
                    | [] ->
                        step.Named
                        |> List.tryPick (fun (k, v) -> if k = "script" || k = "message" then Some v else None)

                let result =
                    Executor.runStep
                        { Name = step.Name
                          Script = script
                          Workspace = cwd
                          Environment = envFor stage
                          TimeoutMs = Some 120_000
                          OnLine = Some(fun l -> output.Add l) }

                // Output arrives exactly once, via OnLine. An earlier version also
                // appended result.Stdout, so every shell line was emitted twice and
                // the differential reported a phantom divergence at line 1.
                //
                // stderr is not streamed by OnLine, so it is added here.
                for line in result.Stderr.Replace("\r\n", "\n").Split '\n' do
                    if line <> "" then output.Add line

                // Jenkins prints its failure reason INTO the build log. Parity
                // requires the same: a diagnostic the user cannot see is not a
                // diagnostic (JB-DUR-005 — Jenkins' own worst behaviour is an
                // opaque `exit code -1`, and we promised to be clearer, not quieter).
                if result.Status <> BuildStatus.Success then
                    result.Diagnostic
                    |> Option.iter (fun d -> output.Add $"ERROR: {d}")

                    status <- BuildStatus.worstOf status result.Status

            let rec runStage (cwd: string) (stage: Stage) =
                if status = BuildStatus.Success then
                    for step in stage.Steps do
                        if status = BuildStatus.Success then
                            match step.Name, step.Positional with
                            | "dir", (sub :: _) ->
                                // `dir('x') { … }` — nested cwd, auto-created
                                match Workspace.resolveUnder cwd sub with
                                | Result.Error e ->
                                    output.Add $"dir refused: {e.Describe}"
                                    status <- BuildStatus.Failure
                                | Result.Ok target ->
                                    Directory.CreateDirectory target |> ignore
                                    for inner in step.Block do
                                        runStep stage target inner
                            | _ -> runStep stage cwd step

                    for nested in stage.Nested do
                        runStage cwd nested

            for stage in pipeline.Stages do
                runStage workspace stage

            let workspaceHash, files = Trace.hashWorkspace workspace

            Result.Ok
                { Result = BuildStatus.toWireString status
                  Output = Trace.normaliseOutput output
                  WorkspaceHash = workspaceHash
                  WorkspaceFiles = files }
