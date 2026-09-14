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

[<Sealed>]
type private BufferedReservationFailure() =
    inherit Exception("test buffered reservation failure")

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

          test "captured stdout stops reserving after its bounded return snapshot" {
              let root = Path.Combine(Path.GetTempPath(), "fogell-capture-return-boundary-" + Guid.NewGuid().ToString("N"))
              Directory.CreateDirectory root |> ignore
              let pidFile = Path.Combine(root, "child.pid")
              let releaseFile = Path.Combine(root, "release")
              let quotedPidFile = shellQuote pidFile
              let quotedReleaseFile = shellQuote releaseFile
              use lateReservation = new Threading.ManualResetEventSlim(false)
              let mutable returned = 0
              // The setsid child intentionally escapes the script group while
              // retaining stdout. It writes only after ProcessGroup's bounded
              // reader wait has returned the captured result.
              let script =
                  $"setsid /bin/sh -c 'printf \"%%s\" \"$$\" > \"$1\"; while [ ! -f \"$2\" ]; do /bin/sleep 0.01; done; printf late' fogell-child {quotedPidFile} {quotedReleaseFile} & "
                  + $"i=0; while [ ! -s {quotedPidFile} ]; do i=$((i+1)); [ \"$i\" -lt 300 ] || exit 97; /bin/sleep 0.01; done; printf pre"

              try
                  let result =
                      ProcessGroup.run
                          { RunRequest.create (script, root) with
                              SuppressStdoutEcho = true
                              ReserveCapturedOutput =
                                  Some(fun _ ->
                                      if Threading.Interlocked.CompareExchange(&returned, 0, 0) = 1 then
                                          lateReservation.Set()) }

                  Threading.Volatile.Write(&returned, 1)
                  Expect.equal result.Stdout "pre" "bytes received before the bounded snapshot remain the return value"
                  Expect.isFalse result.StdoutReachedEof "the escaped writer keeps capture EOF unresolved at return"

                  match waitForPid pidFile with
                  | None -> failtest "the escaped capture writer never established its pid"
                  | Some pid ->
                      File.WriteAllText(releaseFile, "release")
                      Expect.isTrue (waitForReap pid) $"the owned escaped writer {pid} exits after release"

                  Expect.isFalse
                      (lateReservation.Wait 1_000)
                      "late escaped stdout drains without a post-return budget reservation"
              finally
                  try
                      if not (File.Exists releaseFile) then File.WriteAllText(releaseFile, "release")
                      match waitForPid pidFile with
                      | Some pid -> waitForReap pid |> ignore
                      | None -> ()
                      if Directory.Exists root then Directory.Delete(root, true)
                  with _ -> ()
          }

          test "redacted escaped stdout cannot admit after the bounded reader return" {
              let root = Path.Combine(Path.GetTempPath(), "fogell-redacted-return-boundary-" + Guid.NewGuid().ToString("N"))
              Directory.CreateDirectory root |> ignore
              let pidFile = Path.Combine(root, "child.pid")
              let releaseFile = Path.Combine(root, "release")
              let quotedPidFile = shellQuote pidFile
              let quotedReleaseFile = shellQuote releaseFile
              use lateAdmission = new Threading.ManualResetEventSlim(false)
              let mutable returned = 0
              let script =
                  $"setsid /bin/sh -c 'printf \"%%s\" \"$$\" > \"$1\"; while [ ! -f \"$2\" ]; do /bin/sleep 0.01; done; printf \"late\\n\"' fogell-child {quotedPidFile} {quotedReleaseFile} & "
                  + $"i=0; while [ ! -s {quotedPidFile} ]; do i=$((i+1)); [ \"$i\" -lt 300 ] || exit 97; /bin/sleep 0.01; done; printf 'pre\\n'"

              let admission () =
                  let markLate () =
                      if Threading.Interlocked.CompareExchange(&returned, 0, 0) = 1 then
                          lateAdmission.Set()

                  { Admit = fun _ -> markLate ()
                    Buffered =
                        Some
                            { Reserve = fun _ -> markLate ()
                              Admit = fun _ _ -> markLate ()
                              Release = fun _ -> () }
                    // This deliberately observes rather than suppresses late
                    // calls. ProcessGroup's reader freeze must prevent them;
                    // WalkerCtx's production Close is an additional lease.
                    Close = Some ignore
                    Complete = markLate }

              try
                  Expect.throwsT<TimeoutException>
                      (fun () ->
                          ProcessGroup.run
                              { RunRequest.create (script, root) with
                                  OutputRedaction = Some(OutputRedactionPolicy [])
                                  CreateRedactedAdmission = Some admission }
                          |> ignore)
                      "an incomplete redacted reader fails after the bounded wait"

                  Threading.Volatile.Write(&returned, 1)

                  match waitForPid pidFile with
                  | None -> failtest "the escaped redacted writer never established its pid"
                  | Some pid ->
                      File.WriteAllText(releaseFile, "release")
                      Expect.isTrue (waitForReap pid) $"the owned escaped redacted writer {pid} exits after release"

                  Expect.isFalse
                      (lateAdmission.Wait 1_000)
                      "no Reserve, Admit, or Complete call occurs after the reader return boundary"
              finally
                  try
                      if not (File.Exists releaseFile) then File.WriteAllText(releaseFile, "release")
                      match waitForPid pidFile with
                      | Some pid -> waitForReap pid |> ignore
                      | None -> ()
                      if Directory.Exists root then Directory.Delete(root, true)
                  with _ -> ()
          }

          test "a stalled synchronous redacted admission keeps the bounded return" {
              let root = Path.Combine(Path.GetTempPath(), "fogell-redacted-stall-" + Guid.NewGuid().ToString("N"))
              Directory.CreateDirectory root |> ignore
              use entered = new Threading.ManualResetEventSlim(false)
              use release = new Threading.ManualResetEventSlim(false)
              use exited = new Threading.ManualResetEventSlim(false)
              let clock = Stopwatch.StartNew()

              let admission () =
                  { Admit =
                        fun _ ->
                            entered.Set()

                            try
                                release.Wait()
                            finally
                                exited.Set()
                    Buffered = None
                    Close = None
                    Complete = ignore }

              try
                  Expect.throwsT<TimeoutException>
                      (fun () ->
                          ProcessGroup.run
                              { RunRequest.create ("printf 'stalled\\n'", root) with
                                  ReapGroup = false
                                  OutputRedaction = Some(OutputRedactionPolicy [])
                                  CreateRedactedAdmission = Some admission }
                          |> ignore)
                      "a direct stalled admission must not make reader freezing unbounded"

                  Expect.isTrue entered.IsSet "the fixture holds synchronous admission under callbackGate"
                  Expect.isLessThan clock.ElapsedMilliseconds 1_500L "TryEnter reader freeze preserves the existing bounded return"
              finally
                  release.Set()
                  Expect.isTrue (exited.Wait 1_000) "the stalled direct admission exits before fixture cleanup"
                  if Directory.Exists root then Directory.Delete(root, true)
          }

          test "a newline-free redacted fragment reserves before framing and reaps before reporting rejection" {
              let root = Path.Combine(Path.GetTempPath(), "fogell-buffered-reservation-" + Guid.NewGuid().ToString("N"))
              Directory.CreateDirectory root |> ignore
              let pidFile = Path.Combine(root, "child.pid")
              let lateFile = Path.Combine(root, "late.txt")
              let quotedPidFile = shellQuote pidFile
              let quotedLateFile = shellQuote lateFile
              let failure = BufferedReservationFailure()
              let mutable admissionCount = 0
              // The normal stdout stream has no newline. A framer-only quota
              // check would retain the fragment indefinitely and let this
              // child reach its late side effect.
              let script =
                  $"/bin/sh -c 'printf \"%%s\" \"$$\" > \"$1\"; /bin/sleep 600' fogell-child {quotedPidFile} >/dev/null 2>&1 & "
                  + $"i=0; while [ ! -s {quotedPidFile} ]; do i=$((i+1)); [ \"$i\" -lt 300 ] || exit 97; /bin/sleep 0.01; done; "
                  + "head -c 4096 /dev/zero | tr '\\0' x; /bin/sleep 1; touch "
                  + quotedLateFile

              let admission () =
                  // ProcessGroup mints stderr admission before stdout. Do not
                  // let shell tracing on stderr reject before the fixture has
                  // started its child; reject the newline-free stdout chunk.
                  let isStdout = Threading.Interlocked.Increment(&admissionCount) = 2

                  { Admit = ignore
                    Buffered =
                        Some
                            { Reserve =
                                fun _ ->
                                    if isStdout then raise failure
                              Admit = fun _ _ -> ()
                              Release = ignore }
                    Close = None
                    Complete = ignore }

              try
                  try
                      ProcessGroup.run
                          { RunRequest.create (script, root) with
                              GraceMs = 100
                              OutputRedaction = Some(OutputRedactionPolicy [])
                              CreateRedactedAdmission = Some admission }
                      |> ignore
                      failtest "the pre-frame reservation failure was swallowed"
                  with :? BufferedReservationFailure as actual ->
                      Expect.isTrue (obj.ReferenceEquals(actual, failure)) "the original reservation failure survives cleanup"

                  match waitForPid pidFile with
                  | None -> failtest "the child never established reaping evidence"
                  | Some pid ->
                      Expect.isFalse (Directory.Exists(Path.Combine("/proc", string pid))) $"buffered-output failure returned only after reaping child {pid}"

                  Expect.isFalse (File.Exists lateFile) "the parent did not reach its late effect"
              finally
                  try
                      if Directory.Exists root then Directory.Delete(root, true)
                  with _ -> ()
          }

          test "a matcher-held almost-secret prefix reserves before EOF and blocks its late effect" {
              let root = Path.Combine(Path.GetTempPath(), "fogell-matcher-prefix-" + Guid.NewGuid().ToString("N"))
              Directory.CreateDirectory root |> ignore
              let lateFile = Path.Combine(root, "late.txt")
              let prefix = String.replicate 4095 "x"
              let failure = BufferedReservationFailure()
              // The 4095-character output is one character short of the
              // registered form. The matcher deliberately holds it and emits
              // an empty value until EOF; a framer-only reservation therefore
              // permits the sleep/touch on the old implementation.
              let script = "#!/bin/sh\nprintf '%s' \"$PREFIX\"\n/bin/sleep 1\ntouch " + shellQuote lateFile
              let outstandingCredits = ResizeArray<unit -> int>()

              let admission () =
                  let mutable bufferedCharacters = 0
                  outstandingCredits.Add(fun () -> bufferedCharacters)

                  { Admit = ignore
                    Buffered =
                        Some
                            { Reserve =
                                fun characters ->
                                    // A StreamReader may split this prefix at
                                    // any character. Accumulate per-stream
                                    // credit so either chunking reaches the
                                    // same pre-EOF rejection boundary.
                                    if bufferedCharacters + characters >= prefix.Length then
                                        raise failure
                                    bufferedCharacters <- bufferedCharacters + characters
                              Admit = fun _ _ -> failtest "the held prefix must not reach line admission"
                              Release = fun characters -> bufferedCharacters <- bufferedCharacters - characters }
                    Close = None
                    Complete = ignore }

              try
                  try
                      ProcessGroup.run
                          { RunRequest.create (script, root) with
                              Environment = [ "PREFIX", prefix ]
                              GraceMs = 100
                              OutputRedaction = Some(OutputRedactionPolicy [ prefix + "x" ])
                              CreateRedactedAdmission = Some admission }
                      |> ignore
                      failtest "the matcher-prefix reservation failure was swallowed"
                  with :? BufferedReservationFailure as actual ->
                      Expect.isTrue (obj.ReferenceEquals(actual, failure)) "the original prefix reservation failure survives cleanup"

                  Expect.isFalse (File.Exists lateFile) "the held prefix was charged before the script reached its late effect"
                  Expect.isTrue
                      (outstandingCredits |> Seq.forall (fun read -> read () = 0))
                      "cleanup releases partial matcher credit from every stream after rejection"
              finally
                  try
                      if Directory.Exists root then Directory.Delete(root, true)
                  with _ -> ()
          }

          test "buffered framing transfers records and releases CRLF-only credit" {
              let reservations = ResizeArray<int>()
              let admissions = ResizeArray<int * string>()
              let releases = ResizeArray<int>()

              let framer =
                  ProcessGroup.RedactedLineFramer(
                      (fun consumed line -> admissions.Add(consumed, line.Text)),
                      releases.Add,
                      ignore)

              for chunk in [ "one\r"; "\ntwo\rthr"; "ee\n\nfour" ] do
                  reservations.Add chunk.Length
                  framer.Push(RedactedText.Raw chunk)

              framer.Complete()

              Expect.sequenceEqual
                  admissions
                  [ 4, "one"; 4, "two"; 6, "three"; 1, ""; 4, "four" ]
                  "each complete record transfers exactly its retained transformed characters"
              Expect.sequenceEqual releases [ 1 ] "the LF half of CRLF is released instead of double-charged"
              Expect.equal
                  (reservations |> Seq.sum)
                  ((admissions |> Seq.sumBy fst) + (releases |> Seq.sum))
                  "every reserved character is either transferred or released exactly once"
          }

          test "a failed buffered admission releases the unvisited reserved chunk suffix" {
              let mutable outstanding = 0
              let releases = ResizeArray<int>()
              let failure = BufferedReservationFailure()
              let chunk = "first\nsecond"
              outstanding <- chunk.Length

              let framer =
                  ProcessGroup.RedactedLineFramer(
                      (fun consumed _ ->
                          // Buffered.Admit consumes before it can reject, so
                          // the framer must return only bytes it did not visit.
                          outstanding <- outstanding - consumed
                          raise failure),
                      (fun released ->
                          outstanding <- outstanding - released
                          releases.Add released),
                      ignore)

              Expect.throwsT<BufferedReservationFailure>
                  (fun () -> framer.Push(RedactedText.Raw chunk))
                  "the fixture must stop partway through the reserved chunk"
              Expect.sequenceEqual releases [ "second".Length ] "only the unvisited suffix is returned after the failed transfer"
              Expect.equal outstanding 0 "a thrown line admission leaves no buffered credit stranded"
          }

          test "matcher EOF suffix reserves before its unterminated record transfers" {
              let reservations = ResizeArray<int>()
              let admissions = ResizeArray<int * string>()
              let policy = OutputRedactionPolicy [ "secret" ]
              let matcher = policy.CreateMatcher()
              let framer =
                  ProcessGroup.RedactedLineFramer(
                      (fun consumed line -> admissions.Add(consumed, line.Text)),
                      ignore,
                      ignore)

              let prefix = matcher.PushRedacted "secre"
              Expect.equal prefix.Text "" "a matcher prefix remains pending until matcher EOF"

              let suffix = matcher.CompleteRedacted()
              reservations.Add suffix.Text.Length
              framer.Push suffix
              framer.Complete()

              Expect.sequenceEqual reservations [ 5 ] "the EOF suffix is reserved before framing"
              Expect.sequenceEqual admissions [ 5, "secre" ] "the suffix is transferred into its unterminated record"

              let abandoned = policy.CreateMatcher()
              Expect.equal (abandoned.PushRedacted "secre").Text "" "the abandonment fixture retains an ambiguous prefix"
              Expect.equal abandoned.PendingCharacters 5 "the ambiguous prefix occupies matcher state before abandonment"
              abandoned.Abandon()
              Expect.equal abandoned.PendingCharacters 0 "abandonment drops pending matcher state"
              Expect.equal (abandoned.CompleteRedacted()).Text "" "abandonment never turns an unresolved suffix into EOF output"
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
