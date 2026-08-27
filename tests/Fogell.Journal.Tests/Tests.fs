module Fogell.Journal.Tests

open System
open System.Diagnostics
open System.IO
open Expecto
open Fogell.Domain
open Fogell.Journal

let private tempDir () =
    let p = Path.Combine(Path.GetTempPath(), "fogell-journal-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory p |> ignore
    p

let roundTrip =
    testList
        "FG-024 record encoding"
        [ test "every record round-trips" {
              let records =
                  [ StepStarted("Build", 0, "sh")
                    StepFinished("Build", 0, BuildStatus.Success)
                    StepStageWarning("Build", 0, BuildStatus.Unstable)
                    StepFinished("Build", 1, BuildStatus.Failure)
                    StepReason("Build", 1, "script returned exit code 7")
                    StageCommitted "Build"
                    BuildFinished BuildStatus.Unstable ]

              for r in records do
                  Expect.equal (Record.decode (Record.encode r)) (Some r) $"round-trip %A{r}"
          }

          test "a truncated final line is ignored, not fatal" {
              // A crash can tear the last write. Reading must recover everything
              // before it rather than failing the whole attempt.
              let dir = tempDir ()
              let path = Path.Combine(dir, "j.log")

              File.WriteAllText(
                  path,
                  Record.encode (StepStarted("Build", 0, "sh"))
                  + "\n"
                  + Record.encode (StepFinished("Build", 0, BuildStatus.Success))
                  + "\n"
                  + "step-fini")

              let records = Journal.read path
              Expect.equal records.Length 2 "both complete records recovered"
          }

          test "an unparsable line stops the read rather than inventing history" {
              let dir = tempDir ()
              let path = Path.Combine(dir, "j.log")
              File.WriteAllText(path, "garbage\nstep-started\tBuild\t0\tsh\n")
              Expect.isEmpty (Journal.read path) "nothing is trusted after a bad line"
          } ]

let resumePlanning =
    testList
        "FG-025 resume planning"
        [ test "a durably finished step must not execute again" {
              let plan =
                  Resume.plan [ StepStarted("Build", 0, "sh"); StepFinished("Build", 0, BuildStatus.Success) ]

              Expect.equal (Resume.dispositionOf plan "Build" 0) (AlreadyFinished BuildStatus.Success) "finished"
              Expect.isFalse (Resume.shouldExecute plan "Build" 0) "exactly-once"
          }

          test "a step that started without finishing is NOT silently re-run" {
              // Re-running it would be at-least-once, which is exactly the
              // semantics ADR 0003 rejects: for a deploy step that is a second
              // deploy. It is surfaced for reconciliation instead.
              let plan = Resume.plan [ StepStarted("Deploy", 0, "sh") ]
              Expect.equal (Resume.dispositionOf plan "Deploy" 0) Interrupted "interrupted"
              Expect.isFalse (Resume.shouldExecute plan "Deploy" 0) "not re-run"
              Expect.equal plan.NeedsReconciliation [ ("Deploy", 0) ] "reported for reconciliation"
          }

          test "an unreached step is safe to run" {
              let plan = Resume.plan [ StepFinished("Build", 0, BuildStatus.Success) ]
              Expect.equal (Resume.dispositionOf plan "Build" 5) NotStarted "not started"
              Expect.isTrue (Resume.shouldExecute plan "Build" 5) "safe"
          }

          test "a terminal build is not resumable" {
              let plan = Resume.plan [ BuildFinished BuildStatus.Success ]
              Expect.isFalse (Resume.isResumable plan) "terminal"
              Expect.equal plan.Terminal (Some BuildStatus.Success) "carries the result"
          }

          test "a repeated started record does not downgrade a finished step" {
              let plan =
                  Resume.plan
                      [ StepStarted("Build", 0, "sh")
                        StepFinished("Build", 0, BuildStatus.Success)
                        StepStarted("Build", 0, "sh") ]

              Expect.isFalse (Resume.shouldExecute plan "Build" 0) "still exactly-once"
              Expect.isEmpty plan.NeedsReconciliation "not reconciliation"
          }

          test "committed stages are recorded" {
              let plan = Resume.plan [ StageCommitted "Build"; StageCommitted "Test" ]
              Expect.equal plan.CommittedStages (Set.ofList [ "Build"; "Test" ]) "both"
          }

          test "a successful step retains its separate stage warning" {
              let plan =
                  Resume.plan
                      [ StepStarted("Test", 0, "junit")
                        StepStageWarning("Test", 0, BuildStatus.Unstable)
                        StepFinished("Test", 0, BuildStatus.Success) ]

              Expect.equal
                  (Resume.dispositionOf plan "Test" 0)
                  (AlreadyFinished BuildStatus.Success)
                  "the build-facing status stays successful"

              Expect.equal
                  (Resume.stageWarningOf plan "Test" 0)
                  (Some BuildStatus.Unstable)
                  "the stage-only consequence is replayable"
          }

          test "multiple stage warning records retain their worst status" {
              let plan =
                  Resume.plan
                      [ StepStageWarning("Test", 0, BuildStatus.Failure)
                        StepStageWarning("Test", 0, BuildStatus.Unstable) ]

              Expect.equal
                  (Resume.stageWarningOf plan "Test" 0)
                  (Some BuildStatus.Failure)
                  "a later weaker record can never improve a durable warning"
          } ]

/// The board's acceptance criterion, run as a genuine crash: SIGKILL a real
/// process mid-stage, then resume it and prove by marker file that no completed
/// step executed twice.
let crashResume =
    let harness =
        Path.Combine(
            __SOURCE_DIRECTORY__,
            "..",
            "..",
            "tools",
            "Fogell.Crash.Harness",
            "bin",
            "Release",
            "net10.0",
            "fogell-crash-harness.dll")
        |> Path.GetFullPath

    let run (journal: string) (marker: string) (policy: string) (extra: string list) =
        let psi = ProcessStartInfo "dotnet"
        psi.ArgumentList.Add harness
        psi.ArgumentList.Add journal
        psi.ArgumentList.Add marker
        psi.ArgumentList.Add policy
        for e in extra do psi.ArgumentList.Add e
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        use p = Process.Start psi
        let out = p.StandardOutput.ReadToEnd()
        p.WaitForExit 60_000 |> ignore
        p.ExitCode, out

    testList
        "FG-025 exactly-once across a real SIGKILL"
        [ test "harness binary exists" { Expect.isTrue (File.Exists harness) $"expected {harness}" }

          test "a completed step does not re-execute after a crash" {
              let dir = tempDir ()
              let journal = Path.Combine(dir, "attempt.journal")
              let marker = Path.Combine(dir, "executed.txt")

              // step 0 completes, then the process is SIGKILLed inside step 1
              let code, _ = run journal marker "step" [ "--die-at"; "1" ]
              Expect.notEqual code 0 "the harness really died"

              let afterCrash = File.ReadAllLines marker |> Array.toList
              Expect.equal afterCrash [ "executed step-0" ] "only step 0 ran to completion"

              // resume
              let code2, out2 = run journal marker "step" []
              Expect.equal code2 0 $"resume completed: {out2}"

              let afterResume = File.ReadAllLines marker |> Array.toList

              // step 0 must appear EXACTLY once across both runs
              Expect.equal
                  (afterResume |> List.filter ((=) "executed step-0") |> List.length)
                  1
                  "step 0 executed exactly once across the crash"

              // step 1 started but never finished: it is NOT re-run
              Expect.stringContains out2 "skipping step 1" "interrupted step is reported, not repeated"
              Expect.isFalse (afterResume |> List.contains "executed step-1") "interrupted step never re-executed"

              // step 2 was never reached and does run
              Expect.contains afterResume "executed step-2" "unreached step ran on resume"
          }

          test "resuming a completed build is a no-op" {
              let dir = tempDir ()
              let journal = Path.Combine(dir, "attempt.journal")
              let marker = Path.Combine(dir, "executed.txt")

              let code, _ = run journal marker "step" []
              Expect.equal code 0 "first run completed"
              let firstCount = (File.ReadAllLines marker).Length

              let _, out = run journal marker "step" []
              Expect.stringContains out "already-terminal" "recognised as finished"
              Expect.equal (File.ReadAllLines marker).Length firstCount "nothing re-executed"
          } ]

/// FG-135. The attempt dimension: a retry marker SUPERSEDES the stage's step
/// records so far, so the plan's dispositions always describe the latest attempt.
let retryAttempts =
    testList
        "FG-135 retry attempt segmentation"
        [ test "a marker drops the superseded attempt's finished-failure" {
              // the defect shape: without the marker, attempt 1's failure read as
              // durably finished and the resume skipped the step attempt 2 needed
              let plan =
                  Resume.plan
                      [ StepStarted("Flaky", 0, "sh")
                        StepFinished("Flaky", 0, BuildStatus.Failure)
                        RetryAttemptStarted("Flaky", 2)
                        StepStarted("Flaky", 0, "sh") ]

              Expect.equal (Resume.dispositionOf plan "Flaky" 0) Interrupted "the LIVE attempt's state, not the superseded one's"
              Expect.equal plan.NeedsReconciliation [ "Flaky", 0 ] "the interrupted attempt-2 step reconciles"
              Expect.equal (Map.tryFind "Flaky" plan.RetryAttempts) (Some 2) "the journal shows attempt 2 started"
          }

          test "no marker means attempt 1 — every pre-FG-135 journal reads unchanged" {
              let plan =
                  Resume.plan
                      [ StepStarted("Flaky", 0, "sh")
                        StepFinished("Flaky", 0, BuildStatus.Failure) ]

              Expect.equal (Resume.dispositionOf plan "Flaky" 0) (AlreadyFinished BuildStatus.Failure) "unsegmented"
              Expect.isTrue (Map.isEmpty plan.RetryAttempts) "no attempt dimension recorded"
          }

          test "a complete later attempt reads as finished with ITS status" {
              let plan =
                  Resume.plan
                      [ StepStarted("Flaky", 0, "sh")
                        StepFinished("Flaky", 0, BuildStatus.Failure)
                        RetryAttemptStarted("Flaky", 2)
                        StepStarted("Flaky", 0, "sh")
                        StepFinished("Flaky", 0, BuildStatus.Success) ]

              Expect.equal (Resume.dispositionOf plan "Flaky" 0) (AlreadyFinished BuildStatus.Success) "attempt 2's outcome"
              Expect.isEmpty plan.NeedsReconciliation "nothing interrupted"
          }

          test "a marker touches ONLY its own stage" {
              let plan =
                  Resume.plan
                      [ StepFinished("Other", 0, BuildStatus.Success)
                        RetryAttemptStarted("Flaky", 2) ]

              Expect.equal (Resume.dispositionOf plan "Other" 0) (AlreadyFinished BuildStatus.Success) "unrelated stage untouched"
          }

          test "the marker round-trips and refuses nonsense" {
              let r = RetryAttemptStarted("Flaky", 2)
              Expect.equal (Record.decode (Record.encode r)) (Some r) "round-trip"
              // attempt 1 never writes a marker; one on disk is corrupt
              Expect.isNone (Record.decode "retry-attempt\tFlaky\t1") "attempt 1 marker refused"
              Expect.isNone (Record.decode "retry-attempt\tFlaky\tx") "non-numeric refused"
          }

          test "later markers win — the resume continues from the LAST attempt started" {
              let plan =
                  Resume.plan
                      [ RetryAttemptStarted("Flaky", 2)
                        StepStarted("Flaky", 0, "sh")
                        StepFinished("Flaky", 0, BuildStatus.Failure)
                        RetryAttemptStarted("Flaky", 3)
                        StepStarted("Flaky", 0, "sh") ]

              Expect.equal (Map.tryFind "Flaky" plan.RetryAttempts) (Some 3) "last marker"
              Expect.equal (Resume.dispositionOf plan "Flaky" 0) Interrupted "attempt 3's live state"
          }

          test "retry segmentation does not erase an earlier attempt's stage warning" {
              let plan =
                  Resume.plan
                      [ StepStageWarning("Flaky", 0, BuildStatus.Unstable)
                        StepFinished("Flaky", 0, BuildStatus.Success)
                        RetryAttemptStarted("Flaky", 2)
                        StepStarted("Flaky", 0, "sh") ]

              Expect.equal
                  (Resume.stageWarningOf plan "Flaky" 0)
                  (Some BuildStatus.Unstable)
                  "the earlier FlowNode remains inside the Jenkins stage graph"
          } ]

/// FG-207. A failed/aborted completion and the reason that explains it remain
/// two historical wire records, but they are appended as one locked durability
/// group. The internal observer can count only forces that completed through the
/// real Flush(true); it cannot replace production I/O.
let groupedStepDurability =
    let openObserved policy =
        let dir = tempDir ()
        let path = Path.Combine(dir, "grouped.journal")
        let forces = ResizeArray<ForceObservation>()
        let journal = Journal.openAtObserved path policy forces.Add
        path, forces, journal

    let records path = Journal.read path

    testList
        "FG-207 grouped step completion durability"
        [ test "EveryStep writes Finish then Reason and forces the pair exactly once" {
              let path, forces, journal = openObserved EveryStep
              let reason = "script returned exit code 7"
              journal.AppendStepFinished("Gate", 3, BuildStatus.Failure, Some reason)

              Expect.equal
                  (records path)
                  [ StepFinished("Gate", 3, BuildStatus.Failure)
                    StepReason("Gate", 3, reason) ]
                  "the historical bytes and ordering are exact"

              let expectedBytes =
                  [ StepFinished("Gate", 3, BuildStatus.Failure)
                    StepReason("Gate", 3, reason) ]
                  |> List.map (fun record -> Record.encode record + "\n")
                  |> String.concat ""
                  |> Text.Encoding.UTF8.GetBytes

              Expect.sequenceEqual
                  (File.ReadAllBytes path)
                  expectedBytes
                  "the grouped write preserves exact wire lines and the final newline"

              Expect.equal forces.Count 1 "one append-policy force for both records"
              Expect.equal forces[0].Origin AppendPolicy "the automatic policy owns it"
              Expect.equal forces[0].RecordCount 2 "the observation names the whole group"
              Expect.equal forces[0].Length (FileInfo(path).Length) "the force returned after both bytes"
              journal.Close()
          }

          test "the original public two-argument constructor remains available" {
              let dir = tempDir ()
              let path = Path.Combine(dir, "public-constructor.journal")
              use journal = new Journal(path, Never)
              journal.AppendStepFinished("Build", 0, BuildStatus.Success, None)
              Expect.equal (records path) [ StepFinished("Build", 0, BuildStatus.Success) ] "public API"
          }

          test "finish-only completion is one record and one EveryStep force" {
              let path, forces, journal = openObserved EveryStep
              journal.AppendStepFinished("Build", 0, BuildStatus.Success, None)
              Expect.equal (records path) [ StepFinished("Build", 0, BuildStatus.Success) ] "one wire record"
              Expect.equal forces.Count 1 "one automatic force"
              Expect.equal forces[0].RecordCount 1 "the observed group is singleton"
              journal.Close()
          }

          test "EveryStage and Never defer completion force; Sync and Close still force" {
              for policy in [ EveryStage; Never ] do
                  let path, forces, journal = openObserved policy
                  journal.AppendStepFinished("Gate", 0, BuildStatus.Aborted, Some "Rejected")
                  Expect.equal forces.Count 0 $"{policy}: append performs no policy force"
                  Expect.equal (records path).Length 2 $"{policy}: ordinary Flush publishes both records"

                  journal.Sync()
                  Expect.equal forces.Count 1 $"{policy}: explicit Sync forces"
                  Expect.equal forces[0].Origin ExplicitSync $"{policy}: Sync origin"
                  journal.Close()
                  Expect.equal forces.Count 2 $"{policy}: Close independently forces"
                  Expect.equal forces[1].Origin Closing $"{policy}: Close origin"
          }

          test "stage/build boundaries retain the exact automatic policy matrix" {
              for policy, expected in [ EveryStep, 1; EveryStage, 1; Never, 0 ] do
                  let _, forces, journal = openObserved policy
                  journal.Append(StageCommitted "Build")
                  Expect.equal forces.Count expected $"{policy}: StageCommitted force count"
                  journal.Close()

              for policy, expected in [ EveryStep, 1; EveryStage, 1; Never, 0 ] do
                  let _, forces, journal = openObserved policy
                  journal.Append(BuildFinished BuildStatus.Success)
                  Expect.equal forces.Count expected $"{policy}: BuildFinished force count"
                  journal.Close()
          }

          test "reason/status domain refuses green explanations but permits historical unexplained failures" {
              for status in [ BuildStatus.Success; BuildStatus.Unstable ] do
                  let _, _, journal = openObserved EveryStep

                  Expect.throws
                      (fun () -> journal.AppendStepFinished("Build", 0, status, Some "not a failure reason"))
                      $"{status}: a green disposition cannot carry StepReason"

                  journal.Close()

              let path, forces, journal = openObserved EveryStep
              journal.AppendStepFinished("Legacy", 0, BuildStatus.Failure, None)
              journal.AppendStepFinished("Legacy", 1, BuildStatus.Aborted, None)
              Expect.equal
                  (records path)
                  [ StepFinished("Legacy", 0, BuildStatus.Failure)
                    StepFinished("Legacy", 1, BuildStatus.Aborted) ]
                  "capture-site gaps remain readable and writable without invented reasons"
              Expect.equal forces.Count 2 "each finish-only completion is its own durability unit"
              journal.Close()

              let _, _, notBuilt = openObserved EveryStep
              Expect.throws
                  (fun () -> notBuilt.AppendStepFinished("Build", 0, BuildStatus.NotBuilt, None))
                  "NotBuilt is not a finished step"
              notBuilt.Close()
          }

          test "parallel completion groups stay adjacent with exact multiplicity" {
              let path, forces, journal = openObserved Never
              let count = 128

              [| for i in 0 .. count - 1 ->
                     Threading.Tasks.Task.Run(fun () ->
                         journal.AppendStepFinished($"Branch-{i}", i, BuildStatus.Failure, Some $"reason-{i}")) |]
              |> Threading.Tasks.Task.WaitAll

              let written = records path
              Expect.equal written.Length (count * 2) "every finish and reason is present once"
              Expect.equal forces.Count 0 "Never performs no append force under concurrency"

              written
              |> List.chunkBySize 2
              |> List.iter (function
                  | [ StepFinished(stage, index, BuildStatus.Failure); StepReason(reasonStage, reasonIndex, reason) ] ->
                      Expect.equal reasonStage stage "no branch interleaves inside a group"
                      Expect.equal reasonIndex index "reason index matches its finish"
                      Expect.equal reason $"reason-{index}" "reason payload matches its finish"
                  | other -> failtestf "non-adjacent completion group: %A" other)

              journal.Close()
          }

          test "historical nonadjacent, finish-only and torn-reason prefixes retain resume semantics" {
              let historical =
                  Resume.plan
                      [ StepFinished("A", 0, BuildStatus.Failure)
                        StepStarted("B", 0, "sh")
                        StepReason("A", 0, "old nonadjacent explanation") ]

              Expect.equal
                  (Resume.dispositionOf historical "A" 0)
                  (AlreadyFinished BuildStatus.Failure)
                  "a historical nonadjacent reason never changes disposition"

              let dir = tempDir ()
              let path = Path.Combine(dir, "prefix.journal")
              File.WriteAllText(
                  path,
                  Record.encode (StepFinished("Gate", 0, BuildStatus.Aborted))
                  + "\nstep-reason\tGate\t0")

              let prefix = Journal.read path
              Expect.equal prefix [ StepFinished("Gate", 0, BuildStatus.Aborted) ] "torn reason is not trusted"
              Expect.equal
                  (Resume.dispositionOf (Resume.plan prefix) "Gate" 0)
                  (AlreadyFinished BuildStatus.Aborted)
                  "finish-only prefix still skips the completed step"
          } ]

let stepReasons =
    testList
        "FG-114 step reasons"
        [ test "an embedded tab or newline cannot break the 4-field wire format" {
              // The reason is executor output — it can hold anything. Encode
              // flattens; the decoded record is the flattened one, one line.
              let encoded =
                  Record.encode (StepReason("Gate", 1, "line one\nline two\tcolumn"))

              Expect.isFalse (encoded.Contains "\n") "one line on the wire"

              Expect.equal
                  (Record.decode encoded)
                  (Some(StepReason("Gate", 1, "line one line two column")))
                  "flattened, still decodable"
          }

          test "a reason explains a disposition; it never is one" {
              // The reason lands AFTER its failed finish. If the plan read it as
              // a step record, a retry marker's segmentation could drop or keep
              // it differently from the finish it annotates. It must change no
              // disposition, wherever it appears.
              let plan =
                  Resume.plan
                      [ StepStarted("Gate", 0, "input")
                        StepFinished("Gate", 0, BuildStatus.Aborted)
                        StepReason("Gate", 0, "input requires human approval")
                        RetryAttemptStarted("Flaky", 2)
                        StepReason("Flaky", 0, "script returned exit code 7") ]

              Expect.equal (Resume.dispositionOf plan "Gate" 0) (AlreadyFinished BuildStatus.Aborted) "unchanged"
              Expect.equal (Resume.dispositionOf plan "Flaky" 0) NotStarted "a reason is not a start"
              Expect.isEmpty plan.NeedsReconciliation "nothing interrupted"
          } ]

[<EntryPoint>]
let main argv =
    match argv with
    | [| "--fg207-probe"; path |] ->
        let journal = Journal.openAt path EveryStep

        journal.AppendStepFinished(
            "Probe",
            0,
            BuildStatus.Failure,
            Some "script returned exit code 7"
        )

        // Keep the object alive through the observation point without Close's
        // explicit force adding a second syscall to the append-policy probe.
        GC.KeepAlive journal
        0
    | _ ->
        runTestsWithCLIArgs
            []
            argv
            (testSequenced (
                testList
                    "Fogell.Journal"
                    [ roundTrip
                      resumePlanning
                      retryAttempts
                      stepReasons
                      groupedStepDurability
                      crashResume ]
            ))
