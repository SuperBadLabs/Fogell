module Fogell.Differential.BuildOutputIntegrationTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fogell.Differential

// Each command writes 1024 records of 8192 x characters plus a newline.  The
// records stay well below ProcessGroup's per-line limit while four invocations
// exceed the production 32 Mi-character whole-build budget.  Every test keeps
// each individual process below the 16 Mi-character ProcessGroup limit so it exercises
// the run-wide guard rather than the per-process fallback.
let private outputChunk =
    "seq 1024 | awk '{ printf \"%8192s\", \"\"; print \"\" }' | tr ' ' x"

let private groovyDoubleQuoted (command: string) =
    command.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$")

let private sh command =
    $"sh \"{groovyDoubleQuoted command}\""

let private captured command =
    $"sh(script: \"{groovyDoubleQuoted command}\", returnStdout: true)"

let private pipeline steps =
    $"pipeline {{ agent any stages {{ stage('budget') {{ steps {{ {steps} }} }} }} }}"

let private scripted body =
    "pipeline { agent any stages { stage('budget') { steps { script { "
    + body
    + " } } } } }"

let private withRoot label (body: string -> string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), $"fogell-build-output-{label}-{Guid.NewGuid():N}")
    let workspace = Path.Combine(root, "job")

    try
        body root workspace
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

let private expectWholeBuildLimit label result =
    match result with
    | Error why ->
        Expect.stringContains
            why
            "build output exceeded Fogell's whole-build output limit"
            $"{label}: the failure crosses the constant whole-build limit"
    | Ok trace ->
        let bounded lines = lines |> List.map (fun (line: string) -> line.Substring(0, min 200 line.Length))
        failtestf "%s: expected Result.Error, got terminal result %s; notes: %A; first output: %A; last output: %A"
            label trace.Result trace.EngineNotes
            (trace.Output |> List.truncate 8 |> bounded)
            (trace.Output |> List.rev |> List.truncate 20 |> List.rev |> bounded)

let private expectResultErrorWithin label (timeoutMs: int) (run: unit -> Result<'a, string>) =
    let task = Task.Run(fun () -> run ())

    if not (task.Wait timeoutMs) then
        failtestf "%s: runner did not terminate within %d ms" label timeoutMs

    task.GetAwaiter().GetResult()

let buildOutputIntegration =
    testList
        "whole-build output integration"
        [ test "ordinary output charges cumulatively across sub-limit processes" {
              withRoot "ordinary-cumulative" (fun root _ ->
                  let source =
                      pipeline (String.concat "; " [ sh outputChunk; sh outputChunk; sh outputChunk; sh outputChunk ])

                  FogellSide.run [] root "job" source
                  |> expectWholeBuildLimit "ordinary cumulative output")
          }

          test "returnStdout captures charge cumulatively before values are retained" {
              withRoot "capture-cumulative" (fun root workspace ->
                  let body =
                      [ captured outputChunk
                        captured outputChunk
                        captured outputChunk
                        captured outputChunk
                        "sh 'touch capture-after.txt'" ]
                      |> String.concat "; "

                  scripted body
                  |> FogellSide.run [] root "job"
                  |> expectWholeBuildLimit "repeated returnStdout captures"
                  Expect.isFalse (File.Exists(Path.Combine(workspace, "capture-after.txt"))) "no hosted effect follows exhausted captures")
          }

          test "failFast false still reaps a peer after one branch crosses the limit" {
              withRoot "parallel-overflow" (fun root workspace ->
                  let peer =
                      "echo $$ > peer.pid; touch peer-started; sleep 8; touch peer-late.txt"

                  let producerStages =
                      [ 1 .. 4 ]
                      |> List.map (fun i ->
                          let emitter =
                              "while [ ! -f peer-started ]; do sleep 0.01; done; " + outputChunk

                          $"stage('overflow-{i}') {{ steps {{ {sh emitter} }} }}")

                  let source =
                      "pipeline { agent any stages { stage('fanout') { parallel { "
                      + String.concat " " producerStages
                      + $"stage('peer') {{ steps {{ {sh peer} }} }} "
                      + "} } } }"

                  let result =
                      expectResultErrorWithin "parallel whole-build overflow" 20_000 (fun () ->
                          FogellSide.run [] root "job" source)

                  expectWholeBuildLimit "parallel whole-build overflow" result

                  let pidPath = Path.Combine(workspace, "peer.pid")
                  Expect.isTrue (File.Exists pidPath) "peer reached its startup marker before overflow"

                  let peerPid = Int32.Parse(File.ReadAllText pidPath).ToString()
                  Expect.isFalse (Directory.Exists $"/proc/{peerPid}") "the non-failFast peer process was reaped"
                  Expect.isFalse
                      (File.Exists(Path.Combine(workspace, "peer-late.txt")))
                      "the peer did not continue after the global output limit")
          }

          test "nested failFast output overflow is catch-opaque to script and stops later effects" {
              withRoot "nested-catch" (fun root workspace ->
                  let source =
                      "pipeline { agent any stages { stage('outer') { parallel { "
                      + "stage('nested') { failFast true parallel { "
                      + $"stage('overflow') {{ steps {{ sh 'until [ -f nested-peer-started ] && [ -f outer-peer-started ]; do sleep 0.02; done; :'; script {{ try {{ def one = {captured outputChunk}; def two = {captured outputChunk}; def three = {captured outputChunk}; def four = {captured outputChunk}; sh 'touch escaped.txt' }} catch (Exception e) {{ sh 'touch caught.txt' }}; sh 'touch after.txt' }} }} }} "
                      + "stage('nested-peer') { steps { sh 'touch nested-peer-started; sleep 8; touch nested-peer-late.txt' } } "
                      + "} } "
                      + "stage('outer-peer') { steps { sh 'touch outer-peer-started; sleep 8; touch outer-peer-late.txt' } } "
                      + "} } } }"

                  match FogellSide.preflightExecution source with
                  | Error why -> failtestf "nested fixture preflight: %s" why
                  | Ok _ -> ()

                  let result =
                      expectResultErrorWithin "nested output overflow" 20_000 (fun () ->
                          FogellSide.run [] root "job" source)

                  expectWholeBuildLimit "nested output overflow" result
                  for marker in [ "nested-peer-started"; "outer-peer-started" ] do
                      Expect.isTrue (File.Exists(Path.Combine(workspace, marker))) "both peers started before overflow"
                  Expect.isFalse (File.Exists(Path.Combine(workspace, "escaped.txt"))) "try cannot absorb the limit"
                  Expect.isFalse (File.Exists(Path.Combine(workspace, "caught.txt"))) "catch cannot absorb the limit"
                  Expect.isFalse (File.Exists(Path.Combine(workspace, "after.txt"))) "script cannot continue after the limit"
                  Expect.isFalse
                      (File.Exists(Path.Combine(workspace, "nested-peer-late.txt")))
                      "the nested failFast peer stops before its late effect"
                  Expect.isFalse
                      (File.Exists(Path.Combine(workspace, "outer-peer-late.txt")))
                      "the outer sibling also stops after the nested overflow")
          }


          test "persisted overflow drains its prefix, publishes its cause, and permits a fresh control build" {
              withRoot "persisted-overflow" (fun root workspace ->
                  let published = ResizeArray<string>()
                  let hooks =
                      { OnOutput = published.Add
                        IsRestartedRun = false
                        ShouldExecute = fun _ _ -> true
                        StageWasCommitted = fun _ -> false
                        SkippedStatus = fun _ _ -> None
                        SkippedStageWarning = fun _ _ -> None
                        OnStepStarted = fun _ _ _ -> ()
                        OnStepStageWarning = fun _ _ _ -> ()
                        OnStepFinished = fun _ _ _ _ -> ()
                        OnStageCommitted = ignore
                        OnRetryAttempt = fun _ _ -> ()
                        RetryAttemptsSoFar = fun _ -> 1
                        PollInputAnswer = None
                        OnInputClosed = fun _ _ _ -> ()
                        OnInputAnswerVoided = fun _ _ _ -> () }
                  let source =
                      pipeline (String.concat "; " [ sh outputChunk; sh outputChunk; sh outputChunk; sh outputChunk; "sh 'touch forbidden.txt'" ])
                  FogellSide.runPersisted [] root "job" 1 true hooks source
                  |> expectWholeBuildLimit "persisted output overflow"
                  Expect.isFalse (File.Exists(Path.Combine(workspace, "forbidden.txt"))) "no effect follows the overflow"
                  let completeRows = published |> Seq.filter (fun line -> line.Length = 8192 && line[0] = 'x') |> Seq.length
                  Expect.isGreaterThanOrEqual completeRows 3072 "all complete rows from the first three steps drain"
                  Expect.equal published[published.Count - 1]
                      "runner-failure: BUILD_OUTPUT_LIMIT_EXCEEDED: build output exceeded the shared retention limit"
                      "the safe classified failure follows the admitted prefix"
                  published.Clear()
                  match FogellSide.runPersisted [] root "job" 2 true hooks (pipeline "sh 'echo fresh-control'") with
                  | Error why -> failtestf "fresh persisted build refused: %s" why
                  | Ok trace -> Expect.equal trace.Result "success" "the next build has a fresh budget"
                  Expect.contains published "fresh-control" "the next build publishes normally")
          }

          test "three sub-limit chunks and a following control step succeed" {
              withRoot "under-budget-control" (fun root workspace ->
                  let source =
                      pipeline (String.concat "; " [ sh outputChunk; sh outputChunk; sh outputChunk; "sh 'touch control.txt'" ])

                  match FogellSide.run [] root "job" source with
                  | Error why -> failtestf "under-budget control unexpectedly failed: %s" why
                  | Ok trace ->
                      Expect.equal trace.Result "success" "the cumulative output remains below the production budget"
                      Expect.isTrue
                          (File.Exists(Path.Combine(workspace, "control.txt")))
                          "the step after the large but permitted output ran")
          } ]
