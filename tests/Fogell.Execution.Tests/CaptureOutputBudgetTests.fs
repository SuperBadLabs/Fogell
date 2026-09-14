module Fogell.Execution.CaptureOutputBudgetTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Fogell.Execution

[<Sealed>]
type private CaptureReservationFailure() =
    inherit Exception("test capture reservation failure")

[<Sealed>]
type private GeneratedNarrationFailure() =
    inherit Exception("test generated narration failure")

let private shellQuote (value: string) = "'" + value.Replace("'", "'\"'\"'") + "'"

let private waitForPid (path: string) =
    let clock = Stopwatch.StartNew()

    while not (File.Exists path) && clock.ElapsedMilliseconds < 3_000L do
        Threading.Thread.Sleep 10

    if File.Exists path then
        match Int32.TryParse((File.ReadAllText path).Trim()) with
        | true, pid -> Some pid
        | _ -> None
    else
        None

let private waitForReap pid =
    let procPath = Path.Combine("/proc", string pid)
    let clock = Stopwatch.StartNew()

    while Directory.Exists procPath && clock.ElapsedMilliseconds < 3_000L do
        Threading.Thread.Sleep 20

    not (Directory.Exists procPath)

let captureOutputBudget =
    testList
        "captured stdout reservation"
        [ test "a shared capture reservation wakes an unbounded run, reaps its child, and preserves the original exception" {
              let root = Path.Combine(Path.GetTempPath(), "fogell-capture-reservation-" + Guid.NewGuid().ToString("N"))
              Directory.CreateDirectory root |> ignore
              let pidFile = Path.Combine(root, "child.pid")
              let quotedPidFile = shellQuote pidFile
              // Establish the descendant before producing the capture chunk.
              // There is no timeout or interrupt: the reservation signal must
              // wake ProcessGroup's otherwise unbounded wait on its own.
              let script =
                  $"/bin/sh -c 'printf \"%%s\" \"$$\" > \"$1\"; exec /bin/sleep 600' fogell-child {quotedPidFile} >/dev/null 2>&1 & "
                  + $"i=0; while [ ! -s {quotedPidFile} ]; do i=$((i+1)); [ \"$i\" -lt 300 ] || exit 97; /bin/sleep 0.01; done; "
                  + "head -c 4096 /dev/zero | tr '\\0' x; /bin/sleep 30"
              let clock = Stopwatch.StartNew()

              try
                  Expect.throwsT<CaptureReservationFailure>
                      (fun () ->
                          ProcessGroup.run
                              { RunRequest.create (script, root) with
                                  SuppressStdoutEcho = true
                                  GraceMs = 100
                                  ReserveCapturedOutput = Some(fun _ -> raise (CaptureReservationFailure())) }
                          |> ignore)
                      "the reservation exception remains the typed run failure"

                  Expect.isLessThan clock.ElapsedMilliseconds 5_000L "the unbounded wait wakes before the script's delayed effect"

                  match waitForPid pidFile with
                  | None -> failtest "the child never established reaping evidence"
                  | Some pid -> Expect.isTrue (waitForReap pid) $"captured-output failure reaped child {pid}"
              finally
                  try
                      if Directory.Exists root then Directory.Delete(root, true)
                  with _ -> ()
          }

          test "generated interrupt narration failure is deferred until the process group is reaped" {
              let root = Path.Combine(Path.GetTempPath(), "fogell-generated-narration-" + Guid.NewGuid().ToString("N"))
              Directory.CreateDirectory root |> ignore
              let pidFile = Path.Combine(root, "child.pid")
              let releaseFile = Path.Combine(root, "release")
              let lateFile = Path.Combine(root, "late.txt")
              let quotedPidFile = shellQuote pidFile
              let quotedReleaseFile = shellQuote releaseFile
              let quotedLateFile = shellQuote lateFile
              let failure = GeneratedNarrationFailure()
              let script =
                  $"/bin/sh -c 'printf \"%%s\" \"$$\" > \"$1\"; while [ ! -f \"$2\" ]; do /bin/sleep 0.01; done' fogell-child {quotedPidFile} {quotedReleaseFile} >/dev/null 2>&1 & "
                  + $"i=0; while [ ! -s {quotedPidFile} ]; do i=$((i+1)); [ \"$i\" -lt 300 ] || exit 97; /bin/sleep 0.01; done; while [ ! -f {quotedReleaseFile} ]; do /bin/sleep 0.01; done; touch {quotedLateFile}"

              try
                  try
                      ProcessGroup.run
                          { RunRequest.create (script, root) with
                              GraceMs = 100
                              Interrupt = Some(fun () -> File.Exists pidFile)
                              OnGeneratedAdmission =
                                  Some(fun line ->
                                      if line = "Sending interrupt signal to process" then
                                          raise failure) }
                      |> ignore
                      failtest "the generated-admission failure was swallowed"
                  with :? GeneratedNarrationFailure as actual ->
                      Expect.isTrue (obj.ReferenceEquals(actual, failure)) "the original admission failure survives cleanup"

                  match waitForPid pidFile with
                  | None -> failtest "the child never established reaping evidence"
                  | Some pid ->
                      Expect.isFalse (Directory.Exists(Path.Combine("/proc", string pid))) $"generated-admission failure returned only after reaping child {pid}"

                  Expect.isFalse (File.Exists lateFile) "the parent did not reach its late effect"
              finally
                  try
                      File.WriteAllText(releaseFile, "release")
                      match waitForPid pidFile with
                      | Some pid -> waitForReap pid |> ignore
                      | None -> ()
                      if Directory.Exists root then Directory.Delete(root, true)
                  with _ -> ()
          }

          test "synthetic Terminated admission failure follows process-group reaping" {
              let root = Path.Combine(Path.GetTempPath(), "fogell-generated-terminated-" + Guid.NewGuid().ToString("N"))
              Directory.CreateDirectory root |> ignore
              let pidFile = Path.Combine(root, "child.pid")
              let releaseFile = Path.Combine(root, "release")
              let quotedPidFile = shellQuote pidFile
              let quotedReleaseFile = shellQuote releaseFile
              let failure = GeneratedNarrationFailure()
              let script =
                  $"trap '' TERM; /bin/sh -c 'printf \"%%s\" \"$$\" > \"$1\"; while [ ! -f \"$2\" ]; do /bin/sleep 0.01; done' fogell-child {quotedPidFile} {quotedReleaseFile} >/dev/null 2>&1 & "
                  + $"i=0; while [ ! -s {quotedPidFile} ]; do i=$((i+1)); [ \"$i\" -lt 300 ] || exit 97; /bin/sleep 0.01; done; while [ ! -f {quotedReleaseFile} ]; do /bin/sleep 0.01; done"

              try
                  try
                      ProcessGroup.run
                          { RunRequest.create (script, root) with
                              GraceMs = 100
                              Interrupt = Some(fun () -> File.Exists pidFile)
                              OnGeneratedAdmission =
                                  Some(fun line ->
                                      if line = "Terminated" then
                                          raise failure) }
                      |> ignore
                      failtest "the synthetic-Terminated admission failure was swallowed"
                  with :? GeneratedNarrationFailure as actual ->
                      Expect.isTrue (obj.ReferenceEquals(actual, failure)) "the original synthetic-Terminated failure survives cleanup"

                  match waitForPid pidFile with
                  | None -> failtest "the child never established reaping evidence"
                  | Some pid ->
                      Expect.isFalse (Directory.Exists(Path.Combine("/proc", string pid))) $"synthetic-Terminated failure returned only after reaping child {pid}"
              finally
                  try
                      File.WriteAllText(releaseFile, "release")
                      match waitForPid pidFile with
                      | Some pid -> waitForReap pid |> ignore
                      | None -> ()
                      if Directory.Exists root then Directory.Delete(root, true)
                  with _ -> ()
          }

          test "an established process callback failure outranks deferred generated narration" {
              let root = Path.Combine(Path.GetTempPath(), "fogell-generated-precedence-" + Guid.NewGuid().ToString("N"))
              Directory.CreateDirectory root |> ignore
              let pidFile = Path.Combine(root, "child.pid")
              let releaseFile = Path.Combine(root, "release")
              let quotedPidFile = shellQuote pidFile
              let quotedReleaseFile = shellQuote releaseFile
              let narrationFailure = GeneratedNarrationFailure()
              let callbackFailure = InvalidOperationException("test process callback failure")
              use callbackEntered = new Threading.ManualResetEventSlim(false)
              let script =
                  $"echo raw-callback; /bin/sh -c 'printf \"%%s\" \"$$\" > \"$1\"; while [ ! -f \"$2\" ]; do /bin/sleep 0.01; done' fogell-child {quotedPidFile} {quotedReleaseFile} >/dev/null 2>&1 & "
                  + $"i=0; while [ ! -s {quotedPidFile} ]; do i=$((i+1)); [ \"$i\" -lt 300 ] || exit 97; /bin/sleep 0.01; done; while [ ! -f {quotedReleaseFile} ]; do /bin/sleep 0.01; done"

              try
                  try
                      ProcessGroup.run
                          { RunRequest.create (script, root) with
                              GraceMs = 100
                              Interrupt = Some(fun () -> callbackEntered.IsSet && File.Exists pidFile)
                              OnLine =
                                  Some(fun line ->
                                      if line = "raw-callback" then
                                          callbackEntered.Set()
                                          raise callbackFailure)
                              OnGeneratedAdmission =
                                  Some(fun line ->
                                      if line = "Sending interrupt signal to process" then
                                          raise narrationFailure) }
                      |> ignore
                      failtest "the process callback failure was swallowed"
                  with :? InvalidOperationException as actual ->
                      Expect.isTrue (obj.ReferenceEquals(actual, callbackFailure)) "established host output loss outranks deferred narration"

                  match waitForPid pidFile with
                  | None -> failtest "the child never established reaping evidence"
                  | Some pid ->
                      Expect.isFalse (Directory.Exists(Path.Combine("/proc", string pid))) $"precedence failure returned only after reaping child {pid}"
              finally
                  try
                      File.WriteAllText(releaseFile, "release")
                      match waitForPid pidFile with
                      | Some pid -> waitForReap pid |> ignore
                      | None -> ()
                      if Directory.Exists root then Directory.Delete(root, true)
                  with _ -> ()
          } ]
