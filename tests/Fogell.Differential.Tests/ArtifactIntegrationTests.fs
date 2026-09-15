module Fogell.Differential.ArtifactIntegrationTests

open System
open System.IO
open Expecto
open Fogell.Differential
open Fogell.Execution

let private pipeline steps =
    $"pipeline {{ agent any stages {{ stage('artifacts') {{ steps {{ {steps} }} }} }} }}"

let private withPolicy limits body =
    let values = ArtifactPolicy.environmentValues limits
    let previous = values |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable name)
    try
        for name, value in values do Environment.SetEnvironmentVariable(name, value)
        body ()
    finally
        for name, value in previous do Environment.SetEnvironmentVariable(name, value)

let private withRoot body =
    let root = Path.Combine(Path.GetTempPath(), $"fogell-artifact-integration-{Guid.NewGuid():N}")
    try body root
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private limits =
    { MaxFileBytes = 8L; MaxTotalBytes = 12L; MaxFiles = 3; MaxScanEntries = 1000 }

let private expectLimit result =
    match result with
    | Error why -> Expect.stringContains why "Artifact publication limit exceeded." "quota failure survives the walker"
    | Ok trace -> failtestf "expected quota refusal, got %s (%A)" trace.Result trace.EngineNotes

let private hooks (publish: string -> unit) : PersistenceHooks =
    { OnOutput = publish
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

let artifactIntegration =
    testList "bounded artifact integration"
        [ test "separate archive steps share bytes and retain only completed files" {
              withPolicy limits (fun () -> withRoot (fun root ->
                  pipeline "sh 'printf 12345678 > first.bin; printf abcdef > second.bin'; archiveArtifacts 'first.bin'; archiveArtifacts 'second.bin'; sh 'touch forbidden'"
                  |> FogellSide.run [] root "job"
                  |> expectLimit
                  let target = Path.Combine(root, "_artifacts", "job")
                  Expect.equal (File.ReadAllText(Path.Combine(target, "first.bin"))) "12345678" "completed file survives later refusal"
                  Expect.isFalse (File.Exists(Path.Combine(target, "second.bin"))) "over-budget file is never published"
                  Expect.isFalse (File.Exists(Path.Combine(root, "job", "forbidden"))) "later steps do not execute"))
          }
          test "pipeline environment and script catch cannot relax operator policy" {
              withPolicy limits (fun () -> withRoot (fun root ->
                  let source = pipeline "script { try { withEnv(['FOGELL_ARTIFACT_MAX_FILE_BYTES=999999', 'FOGELL_ARTIFACT_MAX_TOTAL_BYTES=999999']) { sh 'printf 123456789 > large.bin'; archiveArtifacts 'large.bin' } } catch (Exception e) { sh 'touch caught' }; sh 'touch after' }"
                  source
                  |> FogellSide.run [ ArtifactPolicy.MaxFileBytesVariable, "999999" ] root "job"
                  |> expectLimit
                  for marker in [ "caught"; "after" ] do
                      Expect.isFalse (File.Exists(Path.Combine(root, "job", marker))) "script cannot absorb a host quota refusal"))
          }
          test "parallel publishers share admission and preserve the classified failure" {
              withPolicy limits (fun () -> withRoot (fun root ->
                  let output = ResizeArray<string>()
                  let source =
                      "pipeline { agent any stages { stage('prepare') { steps { sh 'printf 12345678 > left.bin; printf abcdefgh > right.bin' } } stage('fanout') { parallel { stage('left') { steps { archiveArtifacts 'left.bin' } } stage('right') { steps { archiveArtifacts 'right.bin' } } } } } }"
                  FogellSide.runPersisted [] root "job" 1 true (hooks output.Add) source |> expectLimit
                  let files = Directory.GetFiles(Path.Combine(root, "_artifacts", "job"), "*", SearchOption.AllDirectories)
                  Expect.equal files.Length 1 "only one parallel file fits"
                  Expect.equal (FileInfo(files[0]).Length) 8L "the winner is complete"
                  Expect.contains output
                      "runner-failure: ARTIFACT_LIMIT_EXCEEDED: artifact publication exceeded its retention limit"
                      "parallel joins preserve the safe durable cause"))
          }
          test "persisted refusal permits a fresh control build" {
              withPolicy limits (fun () -> withRoot (fun root ->
                  let output = ResizeArray<string>()
                  FogellSide.runPersisted [] root "bad" 1 true (hooks output.Add)
                      (pipeline "sh 'printf 123456789 > secret-path.bin'; archiveArtifacts 'secret-path.bin'")
                  |> expectLimit
                  Expect.equal output[output.Count - 1]
                      "runner-failure: ARTIFACT_LIMIT_EXCEEDED: artifact publication exceeded its retention limit"
                      "last diagnostic has a constant safe payload"
                  match FogellSide.runPersisted [] root "good" 1 true (hooks output.Add)
                      (pipeline "sh 'printf 12345678 > okay.bin'; archiveArtifacts 'okay.bin'") with
                  | Error why -> failtestf "control build refused: %s" why
                  | Ok trace -> Expect.equal trace.Result "success" "another build has its own capacity"))
          } ]
    |> testSequenced
