module Fogell.Runtime.Tests

open System
open System.Diagnostics
open System.IO
open Expecto
open Fogell.Domain
open Fogell.Journal
open Fogell.Runtime

let withRun source skipped verify =
    let root=Path.Combine(Path.GetTempPath(), "fogell-native-"+Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root,"build")) |> ignore
    let lines=ResizeArray<string>()
    let diagnostics=ResizeArray<ExecutionDiagnostic>()
    let starts=ResizeArray<string>()
    let finishes=ResizeArray<BuildStatus>()
    let hooks={OnOutput=lines.Add;OnDiagnostic=diagnostics.Add;SkippedStatus=skipped
               OnStepStarted=fun _ _ name -> starts.Add name
               OnStepFinished=fun _ _ status _ -> finishes.Add status
               OnStageCommitted=ignore}
    try verify root lines diagnostics starts finishes (Runtime.runPersisted root "build" 1 hooks source)
    finally Directory.Delete(root,true)

let tests = testSequenced <| testList "native runtime" [
    test "commands share workspace, literal text, and declared environment" {
        let source="""{"version":1,"env":{"ANSWER":"42"},"stages":[{"name":"build","steps":[{"run":"printf %s \"$ANSWER\" > result.txt"},{"run":"cat result.txt"},{"echo":"${ANSWER} stays literal"}]}]}"""
        withRun source (fun _ _ -> None) (fun root lines _ starts _ result ->
            Expect.equal (result |> Result.map _.Result) (Ok "success") "terminal success"
            Expect.equal (File.ReadAllText(Path.Combine(root,"build/result.txt"))) "42" "shared workspace"
            Expect.contains lines "${ANSWER} stays literal" "no expression interpreter"
            Expect.equal starts.Count 3 "all steps ran")
    }
    test "failure skips ordinary work but runs explicitly always steps" {
        let source="""{"version":1,"stages":[{"name":"build","steps":[{"run":"exit 7"},{"run":"touch forbidden"},{"echo":"cleanup","always":true}]}]}"""
        withRun source (fun _ _ -> None) (fun root lines diagnostics _ _ result ->
            Expect.equal (result |> Result.map _.Result) (Ok "failure") "failure preserved"
            Expect.isFalse (File.Exists(Path.Combine(root,"build/forbidden"))) "later work skipped"
            Expect.contains lines "cleanup" "cleanup ran"
            Expect.isTrue (diagnostics |> Seq.exists (fun d -> d.ExitCode=Nullable 7)) "typed exit code")
    }
    test "durably completed steps do not repeat effects" {
        let source="""{"version":1,"stages":[{"name":"build","steps":[{"run":"touch forbidden"},{"echo":"after resume"}]}]}"""
        withRun source (fun _ i -> if i=0 then Some BuildStatus.Success else None) (fun root lines _ starts _ result ->
            Expect.equal (result |> Result.map _.Result) (Ok "success") "resume success"
            Expect.isFalse (File.Exists(Path.Combine(root,"build/forbidden"))) "effect not repeated"
            Expect.equal starts.Count 1 "only new step started"
            Expect.contains lines "after resume" "continued")
    }
    test "timeout is reported and descendants are stopped" {
        let source="""{"version":1,"stages":[{"name":"slow","steps":[{"run":"sleep 30","timeout_seconds":1}]}]}"""
        withRun source (fun _ _ -> None) (fun _ _ diagnostics _ _ result ->
            Expect.equal (result |> Result.map _.Result) (Ok "aborted") "deadline abort"
            Expect.isNonEmpty diagnostics "diagnostic exists")
    }
    test "archives and test diagnostics use native operations" {
        let source="""{"version":1,"stages":[{"name":"test","steps":[{"run":"printf '<testsuite><testcase name=\"case\" classname=\"tests\"><failure message=\"broken\"/></testcase></testsuite>' > tests.xml"},{"test_report":"tests.xml"},{"archive":"tests.xml","always":true}]}]}"""
        withRun source (fun _ _ -> None) (fun root _ diagnostics _ _ result ->
            Expect.equal (result |> Result.map _.Result) (Ok "unstable") "test failure retained"
            Expect.isTrue (diagnostics |> Seq.exists (fun d -> d.Category="test" && d.TestName="case")) "typed test failure"
            Expect.isTrue (File.Exists(Path.Combine(root,"_artifacts/build/tests.xml"))) "artifact published")
    }
    test "failed JUnit report remains unstable through the persisted runner" {
        let root=Path.Combine(Path.GetTempPath(), "fogell-native-junit-"+Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let sourcePath=Path.Combine(root,"pipeline.json")
        let workspace=Path.Combine(root,"workspaces")
        let journalPath=Path.Combine(root,"run.journal")
        let runner=Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__,"../../tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host.dll"))
        let source="""{"version":1,"stages":[{"name":"verify","steps":[{"run":"printf '<testsuite><testcase name=\"bad\" classname=\"tests\"><failure message=\"broken\"/></testcase></testsuite>' > report.xml"},{"test_report":"report.xml"},{"echo":"after-report"},{"archive":"report.xml","always":true}]}]}"""
        try
            File.WriteAllText(sourcePath,source)
            let start=ProcessStartInfo("dotnet")
            for argument in [runner;sourcePath;workspace;"build";journalPath] do start.ArgumentList.Add argument
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            use runnerProcess=Process.Start start
            let stdout=runnerProcess.StandardOutput.ReadToEndAsync()
            let stderr=runnerProcess.StandardError.ReadToEndAsync()
            if not (runnerProcess.WaitForExit 30_000) then
                runnerProcess.Kill(true)
                failwith "persisted runner timed out"
            let output=stdout.Result
            let errors=stderr.Result
            Expect.equal runnerProcess.ExitCode 1 $"unstable runner exit; stderr: {errors}"
            Expect.isTrue
                (errors.Split('\n')
                 |> Array.choose ExecutionDiagnostic.decode
                 |> Array.exists (fun item -> item.Category="test" && item.TestName="bad"))
                "typed failed-test diagnostic remained available"
            Expect.isTrue (output.Contains "after-report") "ordinary work after the report ran"
            Expect.isTrue (output.Contains "completed: unstable") "terminal result stayed unstable"
            Expect.isTrue (File.Exists(Path.Combine(workspace,"_artifacts/build/report.xml"))) "always artifact published"
            let records=Journal.read journalPath
            Expect.contains records (StepFinished("verify",1,BuildStatus.Unstable)) "report finish persisted"
            Expect.contains records (StepFinished("verify",2,BuildStatus.Success)) "later step persisted"
            Expect.contains records (StepFinished("verify",3,BuildStatus.Success)) "artifact step persisted"
            Expect.contains records (BuildFinished BuildStatus.Unstable) "terminal journal status persisted"
            Expect.isFalse (records |> List.exists (function StepReason("verify",1,_) -> true | _ -> false)) "unstable step has no failure reason"
        finally Directory.Delete(root,true)
    }
    test "output overflow has a stable infrastructure reason" {
        let source="""{"version":1,"stages":[{"name":"noisy","steps":[{"run":"head -c 18000000 /dev/zero | tr '\\0' x"}]}]}"""
        withRun source (fun _ _ -> None) (fun _ _ diagnostics _ _ result ->
            Expect.equal result (Error "OUTPUT_LIMIT_EXCEEDED") "named bound"
            Expect.isTrue (diagnostics |> Seq.exists (fun d -> d.Message="OUTPUT_LIMIT_EXCEEDED")) "typed resource cause")
    }
    test "a symlinked working directory cannot execute outside the workspace" {
        let source="""{"version":1,"stages":[{"name":"paths","steps":[{"run":"ln -s /tmp outside"},{"run":"touch forbidden","working_directory":"outside"}]}]}"""
        withRun source (fun _ _ -> None) (fun _ _ _ _ finishes result ->
            Expect.equal result (Error "RUN_FAILED") "path refusal"
            Expect.equal finishes.Count 1 "unsafe step did not complete")
    }
    test "lost publication cannot produce terminal success" {
        let root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(root,"build")) |> ignore
        let hooks={OnOutput=(fun _ -> failwith "transport lost");OnDiagnostic=ignore;SkippedStatus=(fun _ _ -> None)
                   OnStepStarted=(fun _ _ _ -> ());OnStepFinished=(fun _ _ _ _ -> ());OnStageCommitted=ignore}
        try
            Expect.throwsT<OutputPublicationException> (fun () ->
                Runtime.runPersisted root "build" 1 hooks """{"version":1,"stages":[{"name":"test","steps":[{"echo":"hello"}]}]}""" |> ignore) "requires reconciliation"
        finally Directory.Delete(root,true)
    }
]

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args tests
