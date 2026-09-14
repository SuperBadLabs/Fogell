module Fogell.Execution.CaptureOutputBudgetTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Fogell.Execution

[<Sealed>]
type private CaptureReservationFailure() =
    inherit Exception("test capture reservation failure")

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
          } ]
