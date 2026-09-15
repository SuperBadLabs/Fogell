module Fogell.Differential.ArtifactIntegrationTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
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

let private pendingBuildId (buildKey: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes buildKey)
    |> Convert.ToHexString
    |> fun value -> value.ToLowerInvariant()

let private persistedArtifactKey (jobName: string) (buildNumber: int) =
    Path.Combine(jobName, $"build@{buildNumber}")

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
                  let files =
                      Directory.GetFiles(
                          Path.Combine(root, "_artifacts", persistedArtifactKey "job" 1),
                          "*",
                          SearchOption.AllDirectories)
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
          }
          test "retained builds publish into independent per-build artifact stores" {
              withPolicy limits (fun () -> withRoot (fun root ->
                  let first = pipeline "sh 'printf 12345678 > first.bin'; archiveArtifacts 'first.bin'"
                  let second = pipeline "sh 'printf abcdefgh > second.bin'; archiveArtifacts 'second.bin'"

                  match FogellSide.runMany [] root "retained-job" [ first; second ] with
                  | [ Ok firstTrace; Ok secondTrace ] ->
                      Expect.equal firstTrace.Result "success" "first retained build has its own quota"
                      Expect.equal secondTrace.Result "success" "second retained build does not inherit the first quota"
                  | results -> failtestf "retained archive sequence failed: %A" results

                  let firstTarget = Path.Combine(root, "_artifacts", "retained-job", "build@1", "first.bin")
                  let secondTarget = Path.Combine(root, "_artifacts", "retained-job", "build@2", "second.bin")
                  Expect.equal (File.ReadAllText firstTarget) "12345678" "first build artifact remains in its own store"
                  Expect.equal (File.ReadAllText secondTarget) "abcdefgh" "second build artifact remains in its own store"))
          }
          test "persisted publication refuses unowned pending data before later steps" {
              withPolicy limits (fun () -> withRoot (fun root ->
                  let output = ResizeArray<string>()
                  let jobName = "cleanup-job"
                  let buildKey = persistedArtifactKey jobName 1
                  let pending =
                      Path.Combine(root, "_artifacts", ".fogell-artifact-pending", pendingBuildId buildKey)
                  let unowned = Path.Combine(pending, "unowned.txt")
                  Directory.CreateDirectory pending |> ignore
                  File.WriteAllText(unowned, "do-not-delete")

                  let source =
                      pipeline "sh 'printf 12345678 > payload.bin'; archiveArtifacts 'payload.bin'; sh 'touch forbidden'"

                  match FogellSide.runPersisted [] root jobName 1 true (hooks output.Add) source with
                  | Ok trace -> failtestf "unowned pending data was accepted: %s" trace.Result
                  | Error _ -> ()

                  Expect.equal output[output.Count - 1]
                      "runner-failure: RUNNER_IO_ERROR: runner input/output operation failed"
                      "persisted failure emits the safe I/O classification"
                  Expect.isTrue (File.Exists unowned) "cleanup preserves unowned pending data"
                  Expect.isFalse (File.Exists(Path.Combine(root, jobName, "forbidden"))) "later steps do not execute"))
          }
          test "persisted build numbers publish into independent artifact stores" {
              withPolicy limits (fun () -> withRoot (fun root ->
                  let jobName = "persisted-job"
                  let first = pipeline "sh 'printf 12345678 > first.bin'; archiveArtifacts 'first.bin'"
                  let second = pipeline "sh 'printf abcdefgh > second.bin'; archiveArtifacts 'second.bin'"

                  match FogellSide.runPersisted [] root jobName 1 true (hooks ignore) first with
                  | Error why -> failtestf "first persisted build failed: %s" why
                  | Ok trace -> Expect.equal trace.Result "success" "first persisted build succeeds"

                  match FogellSide.runPersisted [] root jobName 2 false (hooks ignore) second with
                  | Error why -> failtestf "second persisted build inherited quota: %s" why
                  | Ok trace -> Expect.equal trace.Result "success" "second persisted build has a new quota"

                  let firstTarget = Path.Combine(root, "_artifacts", persistedArtifactKey jobName 1, "first.bin")
                  let secondTarget = Path.Combine(root, "_artifacts", persistedArtifactKey jobName 2, "second.bin")
                  Expect.equal (File.ReadAllText firstTarget) "12345678" "build 1 has its own published bytes"
                  Expect.equal (File.ReadAllText secondTarget) "abcdefgh" "build 2 has its own published bytes"))
          }
          test "resuming a persisted build retains its artifact quota" {
              withPolicy limits (fun () -> withRoot (fun root ->
                  let jobName = "resumed-job"
                  let first = pipeline "sh 'printf 12345678 > first.bin'; archiveArtifacts 'first.bin'"
                  let second = pipeline "sh 'printf abcdefgh > second.bin'; archiveArtifacts 'second.bin'"

                  FogellSide.runPersisted [] root jobName 1 true (hooks ignore) first
                  |> function
                      | Error why -> failtestf "initial persisted build failed: %s" why
                      | Ok _ -> ()

                  FogellSide.runPersisted [] root jobName 1 false (hooks ignore) second
                  |> expectLimit
                  let target = Path.Combine(root, "_artifacts", persistedArtifactKey jobName 1)
                  Expect.isTrue (File.Exists(Path.Combine(target, "first.bin"))) "initial published file remains"
                  Expect.isFalse (File.Exists(Path.Combine(target, "second.bin"))) "resume cannot exceed its original quota"))
          }
          test "explicit persisted artifact key preserves controller UUID staging" {
              withPolicy limits (fun () -> withRoot (fun root ->
                  let jobName = "controller-job"
                  let artifactBuildKey = Guid.NewGuid().ToString "N"
                  let source = pipeline "sh 'printf 12345678 > controller.bin'; archiveArtifacts 'controller.bin'"

                  match
                      FogellSide.runPersistedWithArtifactKey
                          []
                          root
                          jobName
                          artifactBuildKey
                          17
                          true
                          (hooks ignore)
                          source
                  with
                  | Error why -> failtestf "explicit controller artifact key failed: %s" why
                  | Ok trace -> Expect.equal trace.Result "success" "controller-keyed persisted build succeeds"

                  let target = Path.Combine(root, "_artifacts", artifactBuildKey, "controller.bin")
                  Expect.isTrue (File.Exists target) "the supplied UUID remains the exact artifact staging key"
                  Expect.isFalse
                      (Directory.Exists(Path.Combine(root, "_artifacts", persistedArtifactKey jobName 17)))
                      "the numbered default is not substituted for an explicit controller key"))
          } ]
    |> testSequenced
