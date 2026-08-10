module Fogell.Execution.Tests

open System
open System.IO
open Expecto
open Fogell.Domain
open Fogell.Execution
open System.Diagnostics

let private tempRoot () =
    let p = Path.Combine(Path.GetTempPath(), "fogell-exec-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory p |> ignore
    p

let private key () = "attempt-" + Guid.NewGuid().ToString("N").Substring(0, 8)

/// A step request against a freshly created workspace. Creation is an
/// attempt-level concern (FG-030); steps run inside what already exists.
let private request root script =
    let ws =
        match Workspace.createFresh root (key ()) with
        | Result.Ok p -> p
        | Result.Error e -> failwith e.Describe

    { Name = "sh"
      Script = Some script
      Workspace = ws
      Environment = []
      TimeoutMs = None
      CaptureStdout = false
      Interrupt = None
      InterruptBeatsDeadline = None
      WorkspaceRoot = None
      DeadlineExpired = None
      Secrets = []
      OnLine = None
      Named = []
      Artifacts = None
      BuildKey = "test" }

/// Spawn a real background child that records its own pid, then assert on that
/// pid directly.
///
/// The first version of this helper tagged the child via argv (`sleep 600
/// --marker`). That is an INVALID sleep interval, so the child exited
/// immediately and every reaping assertion passed vacuously — the daemon it
/// claimed to reap never existed. Identifying by recorded pid cannot lie.
let private daemonScript (pidFile: string) =
    $"nohup /bin/sh -c 'echo $$ > {pidFile}; exec sleep 600' >/dev/null 2>&1 &"

let private waitForPidFile (pidFile: string) =
    let clock = Diagnostics.Stopwatch.StartNew()

    while not (File.Exists pidFile) && clock.ElapsedMilliseconds < 3_000L do
        Threading.Thread.Sleep 25

    if File.Exists pidFile then
        match Int32.TryParse((File.ReadAllText pidFile).Trim()) with
        | true, pid -> Some pid
        | _ -> None
    else
        None

/// Alive per /proc, which needs no signal and cannot be confused by reuse
/// within a single test.
let private pidAlive (pid: int) = Directory.Exists $"/proc/{pid}"

/// Reaping is asynchronous, so the assertion has to WAIT for it rather than
/// sample once. Both call sites slept a flat 300ms and asserted — which passes
/// or fails by how loaded the machine is, and CI proved it: PR #36 ran the same
/// commit twice and got one red and one green, with the failure detail thrown
/// away by the gate script (fixed alongside).
///
/// Symmetric with [waitForPidFile], which already polls to a deadline for the
/// other half of the same handshake. This cannot weaken the test: a process that
/// is genuinely never reaped still fails, three seconds later.
let private waitForReap (pid: int) =
    let clock = Diagnostics.Stopwatch.StartNew()

    while pidAlive pid && clock.ElapsedMilliseconds < 3_000L do
        Threading.Thread.Sleep 25

    not (pidAlive pid)

let workspaceHygiene =
    testList
        "FG-030 workspace hygiene"
        [ test "a relative path resolves under the root" {
              let root = tempRoot ()

              match Workspace.resolveUnder root "sub/deep" with
              | Result.Ok p -> Expect.stringStarts p (Path.GetFullPath root) "stays under root"
              | Result.Error e -> failtestf "unexpected refusal: %s" e.Describe
          }

          test "an absolute path is refused" {
              match Workspace.resolveUnder (tempRoot ()) "/etc/passwd" with
              | Result.Error(Workspace.AbsolutePath _) -> ()
              | other -> failtestf "expected AbsolutePath, got %A" other
          }

          test "a parent traversal is refused" {
              match Workspace.resolveUnder (tempRoot ()) "../../etc" with
              | Result.Error(Workspace.Traversal _) -> ()
              | other -> failtestf "expected Traversal, got %A" other
          }

          test "a traversal hidden mid-path is refused" {
              match Workspace.resolveUnder (tempRoot ()) "a/../../b" with
              | Result.Error(Workspace.Traversal _) -> ()
              | other -> failtestf "expected Traversal, got %A" other
          }

          test "a symlinked component is refused" {
              let root = tempRoot ()
              let target = Path.Combine(root, "real")
              Directory.CreateDirectory target |> ignore
              let link = Path.Combine(root, "link")
              Directory.CreateSymbolicLink(link, target) |> ignore

              match Workspace.resolveUnder root "link/inner" with
              | Result.Error(Workspace.SymlinkComponent _) -> ()
              | other -> failtestf "expected SymlinkComponent, got %A" other
          }

          test "a fresh workspace is created, and reuse is refused" {
              let root = tempRoot ()
              let k = key ()

              match Workspace.createFresh root k with
              | Result.Ok p -> Expect.isTrue (Directory.Exists p) "created"
              | Result.Error e -> failtestf "unexpected: %s" e.Describe

              match Workspace.createFresh root k with
              | Result.Error(Workspace.AlreadyExists _) -> ()
              | other -> failtestf "reuse must be refused, got %A" other
          } ]

let shellExecution =
    testList
        "FG-040 shell execution"
        [ test "exit code 0 is Success and stdout is captured" {
              let r = Executor.runStep (request (tempRoot ()) "echo hello")
              Expect.equal r.Status Success "success"
              Expect.equal r.ExitCode (Some 0) "exit 0"
              Expect.stringContains r.Stdout "hello" "stdout captured"
              Expect.isNone r.Diagnostic "no diagnostic on success"
          }

          test "a non-zero exit is Failure and the diagnostic names the code" {
              let r = Executor.runStep (request (tempRoot ()) "exit 7")
              Expect.equal r.Status Failure "failure"
              Expect.equal r.ExitCode (Some 7) "code propagated"

              match r.Diagnostic with
              | Some d -> Expect.stringContains d "7" "names the exit code"
              | None -> failtest "a failure must carry a diagnostic"
          }

          test "a shebang script runs as ITS interpreter, untraced" {
              // durable-task executes a shebang script directly, injecting no -xe:
              // a bash script runs under bash, and no `+` trace appears.
              let r = Executor.runStep (request (tempRoot ()) "#!/bin/bash\necho ran-as:$0")
              Expect.stringContains r.Stdout "ran-as:" "the script executed"
              Expect.isFalse (r.Stdout.Contains "+ echo") "no injected trace"
          }

          test "stderr merges into the ordered stream, exactly as Jenkins' console does" {
              // FG-102: the shell runs `2>&1`, because the xtrace lives on stderr and
              // two async pipe readers deliver cross-stream events in racy order —
              // output lines overtook their own `+` trace. One pipe is kernel-ordered,
              // and Jenkins' console is the same merged stream.
              let r = Executor.runStep (request (tempRoot ()) "echo oops >&2")
              Expect.stringContains r.Stdout "oops" "stderr content arrives in the ordered stream"
          }

          test "output streams while the step runs, not only at the end" {
              let seen = System.Collections.Concurrent.ConcurrentBag<string>()
              let root = tempRoot ()

              let r =
                  Executor.runStep
                      { request root "for i in 1 2 3; do echo tick-$i; sleep 0.2; done" with
                          OnLine = Some(fun l -> seen.Add l) }

              Expect.equal r.Status Success "succeeded"
              Expect.isGreaterThanOrEqual (seen |> Seq.filter (fun l -> l.StartsWith "tick-") |> Seq.length) 3 "all lines streamed"
          }

          test "the step runs inside its own fresh workspace" {
              let root = tempRoot ()
              let req = request root "pwd"
              let r = Executor.runStep req
              Expect.stringContains r.Stdout (Path.GetFileName req.Workspace) "cwd is the attempt workspace"
          }

          test "environment is passed through" {
              let r =
                  Executor.runStep
                      { request (tempRoot ()) "echo v=$FOGELL_TEST" with
                          Environment = [ "FOGELL_TEST", "42" ] }

              Expect.stringContains r.Stdout "v=42" "env visible"
          }

          test "an unimplemented step fails closed with a named reason" {
              let r =
                  Executor.runStep
                      { request (tempRoot ()) "" with
                          Name = "kubernetesDeploy"
                          Script = None }

              Expect.equal r.Status Failure "fails closed"

              match r.Diagnostic with
              | Some d ->
                  Expect.stringContains d "kubernetesDeploy" "names the step"
                  Expect.stringContains d "fails closed" "says why"
              | None -> failtest "must carry a diagnostic"
          } ]

let containment =
    testList
        "FG-031/032 process group containment"
        [ test "the step leads its own process group" {
              let r = Executor.runStep (request (tempRoot ()) "echo x")
              Expect.isSome r.ProcessGroupId "a group id was observed"
          }

          test "a 30-day budget is represented exactly, not wrapped" {
              // FG-103 acceptance. `timeout(time: 30, unit: 'DAYS')` is
              // 2,592,000,000 ms — past Int32.MaxValue. This narrowing wrapped
              // negative TWICE in this project's history, both times aborting a
              // valid step instantly; the budget travels int64 to the executor and
              // this run must complete, not time out at t=0.
              let r =
                  ProcessGroup.run
                      { RunRequest.create ("echo wide", tempRoot ()) with
                          TimeoutMs = Some 2_592_000_000L }

              Expect.equal r.Outcome (Completed 0) "a huge budget is a budget, not an instant abort"
          }

          test "a timeout sends a trappable SIGTERM before killing" {
              // The script traps TERM and reports it, proving the interrupt is
              // the contract Jenkins offers (JB-FAIL-003) rather than SIGKILL.
              let root = tempRoot ()
              let marker = Path.Combine(root, "trapped.txt")

              let r =
                  Executor.runStep
                      { request root $"trap 'echo caught > {marker}; exit 0' TERM; sleep 30" with
                          TimeoutMs = Some 1_000 }

              Expect.equal r.Status Aborted "aborted on timeout"

              match r.Diagnostic with
              | Some d -> Expect.stringContains d "timeout" "diagnostic explains the abort"
              | None -> failtest "an abort must carry a diagnostic"

              // the handler had a grace window and used it
              Expect.isTrue (File.Exists marker) "the TERM handler ran"

              match r.Termination with
              | Some t -> Expect.isTrue t.GracefulExit "exited on TERM without escalation"
              | None -> failtest "termination detail expected"
          }

          test "a step ignoring SIGTERM is escalated to SIGKILL" {
              let r =
                  Executor.runStep
                      { request (tempRoot ()) "trap '' TERM; sleep 30" with
                          TimeoutMs = Some 800
                          }

              Expect.equal r.Status Aborted "aborted"

              match r.Termination with
              | Some t ->
                  Expect.isTrue t.Escalated "escalated to SIGKILL"
                  Expect.equal t.LeakedProcesses 0 "nothing survived"
              | None -> failtest "termination detail expected"
          }

          test "BEAT JENKINS: a backgrounded child is reaped after SUCCESS" {
              // Measured on Jenkins (JB-FAIL-004): a nohup'ed child survives both
              // success and abort, and JENKINS_NODE_COOKIE=dontKillMe is moot
              // because nothing is killed. Fogell reaps the group.
              let root = tempRoot ()
              let req = request root ""
              let pidFile = Path.Combine(req.Workspace, "daemon.pid")
              let r = Executor.runStep { req with Script = Some(daemonScript pidFile + " echo spawned") }

              Expect.equal r.Status Success "step itself succeeded"

              match waitForPidFile pidFile with
              | None -> failtest "the daemon never started, so this test would prove nothing"
              | Some pid ->
                  Expect.isTrue (waitForReap pid) $"daemon {pid} must be reaped after success"

                  match r.Termination with
                  | Some t -> Expect.equal t.LeakedProcesses 0 "no leak reported"
                  | None -> failtest "reaping should report a termination"
          }

          test "BEAT JENKINS: a backgrounded child is reaped after a TIMEOUT too" {
              let root = tempRoot ()
              let req = request root ""
              let pidFile = Path.Combine(req.Workspace, "daemon.pid")

              let r =
                  Executor.runStep
                      { req with
                          Script = Some(daemonScript pidFile + " sleep 30")
                          TimeoutMs = Some 1_200 }

              Expect.equal r.Status Aborted "timed out"

              match waitForPidFile pidFile with
              | None -> failtest "the daemon never started, so this test would prove nothing"
              | Some pid ->
                  Expect.isTrue (waitForReap pid) $"daemon {pid} must be reaped on abort"
          }

          test "reaping can be opted out of" {
              let root = tempRoot ()

              let ws =
                  match Workspace.createFresh root (key ()) with
                  | Result.Ok p -> p
                  | Result.Error e -> failtestf "%s" e.Describe

              let pidFile = Path.Combine(ws, "daemon.pid")

              let r =
                  ProcessGroup.run
                      { RunRequest.create (daemonScript pidFile + " echo spawned", ws) with
                          ReapGroup = false }

              Expect.equal r.Outcome (Completed 0) "succeeded"
              Expect.isNone r.Termination "no reaping was attempted"

              match waitForPidFile pidFile with
              | None -> failtest "the daemon never started, so this test would prove nothing"
              | Some pid ->
                  Expect.isTrue (pidAlive pid) $"the opt-out must keep daemon {pid} alive"
                  // leave nothing behind
                  Native.signalProcess pid Native.SIGKILL |> ignore
          } ]

/// FG-070/071. The properties that make secret handling better than Jenkins',
/// each asserted against a real subprocess.
let secrets =
    testList
        "FG-070/071 secret delivery and leak detection"
        [ test "the value IS bound, as Jenkins binds it — and a file companion too" {
              // CLAIM CORRECTED, twice, and this test is the record of it.
              //
              // Originally: "the value is NOT in the child's environment", on the
              // measurement that a secret in `environ` is readable from
              // /proc/<pid>/environ by any same-UID process.
              //
              // First correction (Codex, PR #11): the 0600 file does not defeat that
              // attacker either — it reads `TOKEN_FILE` from environ and opens the file,
              // which it owns.
              //
              // Second correction (FG-044 measurement): Jenkins' `withCredentials` binds
              // the VALUE — `env | grep -c '^TOKEN='` is 1, `${#TOKEN}` is the secret's
              // length — and every real pipeline reads `$TOKEN`. Binding only a path
              // breaks lift-and-shift for all 23 corpus files that use credentials, and
              // lift-and-shift is the product. So Fogell binds the value for parity AND
              // offers the file companion, and what actually protects the value is
              // masking on every output path (FG-071), not absence from the environment.
              let root = tempRoot ()
              let req = request root ""
              let binding = Secrets.bind req.Workspace "TOKEN" "SUPERSECRET123"

              let r =
                  Executor.runStep
                      { req with
                          // the script prints its OWN environment
                          Script = Some "cat /proc/self/environ | tr '\\0' '\\n' | sort"
                          Environment = Secrets.environmentFor [ binding ]
                          Secrets = [ binding ] }

              Expect.equal r.Status BuildStatus.Success "ran"
              Expect.stringContains r.Stdout "TOKEN=" "the value variable is bound, as Jenkins binds it"
              Expect.stringContains r.Stdout "TOKEN_FILE=" "and the file companion is bound too"

              // REVIEW FIX (Copilot, PR #15): this test prints the child's whole
              // environment, so it is the strongest available place to assert that the
              // masker holds — and when I rewrote it for the parity change I asserted only
              // that the variables EXIST, which a real leak would sail through. The value
              // is now in the environment by design, so masking is the entire protection
              // and this is where it gets proven.
              Expect.isFalse (r.Stdout.Contains "SUPERSECRET123") "the value is MASKED on the way out"
              Expect.stringContains r.Stdout "****" "and replaced with the mask token"
              Secrets.revoke [ binding ]
          }

          test "a binary file credential reports no phantom literal leak" {
              // REVIEW FIX (Codex, PR #15 round 5): `bindBytes` stores an empty Value for a
              // non-UTF-8 credential, and every string contains the empty string — so a
              // literal leak was reported on EVERY line of output inside the block. A
              // warning that always fires trains the reader to ignore the channel.
              let root = tempRoot ()
              let bytes = [| 0uy; 159uy; 146uy; 150uy; 255uy |] // invalid UTF-8
              let binding = Secrets.bindBytes root "CERT" bytes

              Expect.isEmpty (Secrets.detectLeaks [ binding ] "ordinary build output") "no phantom leak"
              Expect.equal (Secrets.mask [ binding ] "ordinary build output") "ordinary build output" "output untouched"
              Secrets.revoke [ binding ]
          }

          test "an explicitly requested variable is never overridden by a companion" {
              // Requested `TOKEN_FILE` must survive another binding's generated
              // `TOKEN_FILE` companion, or the body gets a path where its credential
              // should be.
              let root = tempRoot ()
              let explicitBinding = Secrets.bind root "TOKEN_FILE" "explicit-secret"
              let other = Secrets.bind root "TOKEN" "other-secret"

              let env = Secrets.environmentFor [ explicitBinding; other ] |> Map.ofList

              Expect.equal (Map.tryFind "TOKEN_FILE" env) (Some "explicit-secret") "the requested value wins"
              Secrets.revoke [ explicitBinding; other ]
          }

          test "a stash name cannot escape the stash root" {
              // Both reviewers flagged this independently on PR #15, and it was
              // destructive: `save` deletes its target recursively before recreating it,
              // so `stash name: '../../x'` would have removed whatever that resolved to,
              // and `unstash` would copy arbitrary controller files into the workspace.
              // The name comes from a Jenkinsfile, which is untrusted third-party code.
              let root = tempRoot ()
              let store = StashStore.under (Path.Combine(root, "stashes"))
              let ws = match Workspace.createFresh root (key ()) with
                       | Result.Ok p -> p
                       | Result.Error e -> failtestf "%s" e.Describe

              File.WriteAllText(Path.Combine(ws, "f.txt"), "body")

              let canary = Path.Combine(root, "canary.txt")
              File.WriteAllText(canary, "must survive")

              for hostile in [ "../../canary"; "/etc"; "..\\..\\canary"; "a/../../b" ] do
                  let saved, _ = Stash.save store "build-1" ws hostile [ "f.txt" ] [] (fun () -> false)
                  Expect.equal saved [ "f.txt" ] $"the stash still works for name '{hostile}'"

              Expect.isTrue (File.Exists canary) "nothing outside the stash root was touched"
              Expect.equal (File.ReadAllText canary) "must survive" "and it was not overwritten"

              // Distinct hostile names must not collide with each other either.
              let a, _ = Stash.save store "build-1" ws "x" [ "f.txt" ] [] (fun () -> false)
              let b, _ = Stash.save store "build-1" ws "y" [ "f.txt" ] [] (fun () -> false)
              Expect.equal a b "both saved"

              match Stash.restore store "build-1" ws "x" (fun () -> false) with
              | Ok files -> Expect.equal files [ "f.txt" ] "restoring 'x' finds its own content"
              | Error e -> failtest e
          }

          test "the hardened path-only form is still available for a caller that wants it" {
              // The original FG-070 behaviour has not been deleted, only demoted from
              // the default: a caller that accepts the incompatibility can still have it.
              let root = tempRoot ()
              let req = request root ""
              let binding = Secrets.bind req.Workspace "TOKEN" "SUPERSECRET123"

              let r =
                  Executor.runStep
                      { req with
                          Script = Some "cat /proc/self/environ | tr '\\0' '\\n' | sort"
                          Environment = Secrets.environmentForPathOnly [ binding ] }

              Expect.isFalse (r.Stdout.Contains "SUPERSECRET123") "no value in the environment"
              Expect.stringContains r.Stdout "TOKEN_FILE=" "only a path"
              Secrets.revoke [ binding ]
          }

          test "the secret file is readable by its owner and nobody else" {
              let root = tempRoot ()
              let ws = match Workspace.createFresh root (key ()) with
                       | Result.Ok p -> p
                       | Result.Error e -> failtestf "%s" e.Describe

              let binding = Secrets.bind ws "TOKEN" "SUPERSECRET123"
              let mode = File.GetUnixFileMode binding.FilePath

              Expect.isTrue (mode.HasFlag UnixFileMode.UserRead) "owner may read"
              Expect.isFalse (mode.HasFlag UnixFileMode.GroupRead) "group may not"
              Expect.isFalse (mode.HasFlag UnixFileMode.OtherRead) "others may not"
              Secrets.revoke [ binding ]
          }

          test "a script can still use the secret via its file" {
              let root = tempRoot ()
              let req = request root ""
              let binding = Secrets.bind req.Workspace "TOKEN" "SUPERSECRET123"

              let r =
                  Executor.runStep
                      { req with
                          Script = Some "test -s \"$TOKEN_FILE\" && echo have-secret"
                          Environment = Secrets.environmentFor [ binding ] }

              Expect.stringContains r.Stdout "have-secret" "usable"
              Secrets.revoke [ binding ]
          }

          test "masking covers the same forms Jenkins covers" {
              let root = tempRoot ()
              let ws = match Workspace.createFresh root (key ()) with
                       | Result.Ok p -> p
                       | Result.Error e -> failtestf "%s" e.Describe

              let b = Secrets.bind ws "TOKEN" "SUPERSECRET123"
              let masked = Secrets.mask [ b ] "value=SUPERSECRET123 b64=U1VQRVJTRUNSRVQxMjM= low=supersecret123"
              Expect.isFalse (masked.Contains "SUPERSECRET123") "literal masked"
              Expect.isFalse (masked.Contains "U1VQRVJTRUNSRVQxMjM=") "base64 masked"
              Expect.isFalse (masked.Contains "supersecret123") "lowercase masked"
              Secrets.revoke [ b ]
          }

          test "BEAT JENKINS: a transformation that defeats masking is REPORTED" {
              // Jenkins leaks on rev/hex/substring silently, with the build green.
              // Fogell does not mask those either — masking every encoding is
              // impossible — but it detects them and names the encoding, which is
              // the difference between a known gap and a silent one.
              let root = tempRoot ()
              let ws = match Workspace.createFresh root (key ()) with
                       | Result.Ok p -> p
                       | Result.Error e -> failtestf "%s" e.Describe

              let b = Secrets.bind ws "TOKEN" "SUPERSECRET123"
              let reversed = String(("SUPERSECRET123").ToCharArray() |> Array.rev)
              let leaks = Secrets.detectLeaks [ b ] (Secrets.mask [ b ] $"out={reversed}")

              Expect.isNonEmpty leaks "the reversed form is detected"
              Expect.equal (leaks |> List.map (fun l -> l.Encoding)) [ "reversed" ] "names the encoding"
              Secrets.revoke [ b ]
          }

          test "clean output produces no leak report" {
              let root = tempRoot ()
              let ws = match Workspace.createFresh root (key ()) with
                       | Result.Ok p -> p
                       | Result.Error e -> failtestf "%s" e.Describe

              let b = Secrets.bind ws "TOKEN" "SUPERSECRET123"
              Expect.isEmpty (Secrets.detectLeaks [ b ] (Secrets.mask [ b ] "nothing to see")) "no false positives"
              Secrets.revoke [ b ]
          }

          test "revoke removes the secret file" {
              let root = tempRoot ()
              let ws = match Workspace.createFresh root (key ()) with
                       | Result.Ok p -> p
                       | Result.Error e -> failtestf "%s" e.Describe

              let b = Secrets.bind ws "TOKEN" "SUPERSECRET123"
              Expect.isTrue (File.Exists b.FilePath) "written"
              Secrets.revoke [ b ]
              Expect.isFalse (File.Exists b.FilePath) "removed"
          } ]

/// FG-033. Jenkins takes ~10 minutes to notice a dead step process and then says
/// `exit code -1`. Fogell owns the process.
let deadProcessDetection =
    testList
        "FG-033 external termination"
        [ test "BEAT JENKINS: an externally killed step is detected in seconds and names the signal" {
              let root = tempRoot ()
              let req = request root ""
              let pidFile = Path.Combine(req.Workspace, "child.pid")

              // the step records its own pid, then sleeps; an external killer
              // SIGKILLs it, exactly as an operator or OOM killer would
              let killer =
                  async {
                      do! Async.Sleep 700

                      match waitForPidFile pidFile with
                      | Some pid -> Native.signalProcess pid Native.SIGKILL |> ignore
                      | None -> ()
                  }

              Async.Start killer
              let sw = Diagnostics.Stopwatch.StartNew()

              let r =
                  Executor.runStep
                      { req with
                          Script = Some $"echo $$ > {pidFile}; sleep 60"
                          TimeoutMs = Some 30_000 }

              sw.Stop()

              Expect.isLessThan sw.ElapsedMilliseconds 10_000L "detected in seconds, not minutes"
              Expect.equal r.Status BuildStatus.Failure "reported as a failure"

              match r.Diagnostic with
              | Some d ->
                  Expect.stringContains d "SIGKILL" "names the signal"
                  Expect.stringContains d "outside the engine" "distinguishes it from our own timeout"
                  Expect.stringContains d "may be incomplete" "warns about the effect"
              | None -> failtest "an external kill must carry a diagnostic"
          }

          test "our own timeout is NOT reported as an external kill" {
              let r =
                  Executor.runStep
                      { request (tempRoot ()) "" with
                          Script = Some "trap '' TERM; sleep 30"
                          TimeoutMs = Some 700 }

              Expect.equal r.Status BuildStatus.Aborted "a timeout is Aborted, not Failure"

              match r.Diagnostic with
              | Some d ->
                  Expect.stringContains d "timeout" "named as a timeout"
                  Expect.isFalse (d.Contains "outside the engine") "not attributed to an external actor"
              | None -> failtest "expected a diagnostic"
          } ]

let externalInterrupt =
    testList
        "FG-036 external interruption of a running step"
        [ test "an interrupt stops a running step and reports Aborted, not TimedOut" {
              // failFast needs to stop a sibling that is ALREADY running. A flag
              // checked only between steps cannot do that, so the interrupt is
              // polled while the process runs and takes the same
              // SIGTERM -> grace -> SIGKILL path a timeout takes (JB-FAIL-003).
              let root = tempRoot ()
              let marker = Path.Combine(root, "trapped.txt")
              let stop = ref false

              // Release the interrupt shortly after the step starts, so the
              // step is genuinely mid-flight and not merely never launched.
              let releaser =
                  System.Threading.Tasks.Task.Run(fun () ->
                      System.Threading.Thread.Sleep 700
                      stop.Value <- true)

              let sw = Diagnostics.Stopwatch.StartNew()

              let r =
                  Executor.runStep
                      { request root $"trap 'echo caught > {marker}; exit 0' TERM; sleep 30" with
                          // 60s: far longer than the test tolerates, so a pass
                          // cannot come from the timeout path by accident.
                          TimeoutMs = Some 60_000
                          Interrupt = Some(fun () -> stop.Value) }

              sw.Stop()
              releaser.Wait()

              Expect.equal r.Status Aborted "interrupted step is aborted"
              Expect.isLessThan sw.ElapsedMilliseconds 15_000L "interrupted promptly, not on the 60s timeout"
              Expect.isTrue (File.Exists marker) "the TERM handler ran — the interrupt is trappable"

              match r.Diagnostic with
              | Some d ->
                  // The cause must not be misreported. A step interrupted by a
                  // sibling did not run out of time, and calling it a timeout
                  // sends the operator to the wrong place.
                  Expect.isFalse (d.ToLowerInvariant().Contains "timeout") $"cause is not a timeout: {d}"
              | None -> failtest "an abort must carry a diagnostic"
          }

          test "an expired timeout is reported as a TIMEOUT, not a cancellation" {
              // REVIEW FIX (Codex, PR #14 round 6): folding the deadline into the
              // interrupt predicate made ProcessGroup classify an expired timeout as
              // `Cancelled`, so the diagnostic said "step was cancelled" and stopped
              // naming the timeout — losing exactly the cause distinction FG-033
              // exists to preserve.
              let r =
                  Executor.runStep
                      { request (tempRoot ()) "sleep 30" with
                          TimeoutMs = Some 800L
                          DeadlineExpired = Some(fun () -> true) }

              Expect.equal r.Status Aborted "aborted"

              match r.Diagnostic with
              | Some d -> Expect.stringContains d "timeout" $"the cause is still named a timeout: {d}"
              | None -> failtest "an abort must carry a diagnostic"
          }

          test "junit honours an expired deadline instead of returning a result" {
              // REVIEW FIX (Codex, PR #14 round 9): DeadlineExpired was DOCUMENTED as
              // polled by "archive, junit" and only archive read it, so a `timeout`
              // whose last step is `junit` could scan reports and return Success or
              // Unstable after the deadline — leaving the build non-aborted.
              let root = tempRoot ()
              let ws = match Workspace.createFresh root (key ()) with
                       | Result.Ok p -> p
                       | Result.Error e -> failwith e.Describe

              File.WriteAllText(
                  Path.Combine(ws, "report.xml"),
                  "<testsuite tests=\"1\" failures=\"0\" skipped=\"0\"/>")

              let r =
                  Executor.runStep
                      { Name = "junit"
                        Script = None
                        Workspace = ws
                        Environment = []
                        TimeoutMs = None
                        CaptureStdout = false
                        Interrupt = None
                        InterruptBeatsDeadline = None
                        WorkspaceRoot = None
                        DeadlineExpired = Some(fun () -> true)
                        Secrets = []
                        OnLine = None
                        Named = [ "testResults", "report.xml" ]
                        Artifacts = None
                        BuildKey = "k" }

              // Asserting only `notEqual Success` was too weak — it passes for Failure,
              // which is exactly the bug Codex found in round 10: an interrupted junit
              // returned Failure, so a timeout selected `post { failure }` where shell
              // and archive timeouts select `post { aborted }`. Assert the exact status.
              Expect.equal r.Status Aborted "an interrupted junit is ABORTED, not failed"

              // Round-12 mirror: with NO matching report, an interrupt must still be an
              // abort rather than "no test report matched the pattern" — which would
              // send the user off debugging a glob that was fine.
              let noMatch =
                  Executor.runStep
                      { Name = "junit"
                        Script = None
                        Workspace = ws
                        Environment = []
                        CaptureStdout = false
                        TimeoutMs = None
                        Interrupt = None
                        InterruptBeatsDeadline = None
                        WorkspaceRoot = None
                        DeadlineExpired = Some(fun () -> true)
                        Secrets = []
                        OnLine = None
                        Named = [ "testResults", "nothing-matches-*.xml" ]
                        Artifacts = None
                        BuildKey = "k" }

              Expect.equal noMatch.Status Aborted "zero-match plus interrupt is an abort"

              match noMatch.Diagnostic with
              | Some d -> Expect.isFalse (d.Contains "no test report matched") $"not blamed on the pattern: {d}"
              | None -> failtest "expected a diagnostic"

              match r.Diagnostic with
              | Some d -> Expect.stringContains d "aborted" $"the abort is named: {d}"
              | None -> failtest "an aborted junit must carry a diagnostic"
          }

          test "an interrupt that never fires leaves the step alone" {
              // Guard against the previous class of vacuous test: if the poll
              // were treated as truthy, or called once at the wrong moment, the
              // test above would still pass. This one fails if it is.
              let r =
                  Executor.runStep
                      { request (tempRoot ()) "echo done" with
                          TimeoutMs = Some 30_000
                          Interrupt = Some(fun () -> false) }

              Expect.equal r.Status Success "an un-fired interrupt changes nothing"
              Expect.stringContains r.Stdout "done" "the step ran to completion"
          } ]



let maskingOnOutputPath =
    testList
        "FG-071 masking is ON the output path, not merely available"
        [ test "a secret echoed by the step is masked in BOTH streamed and buffered output" {
              // REVIEW FIX (Codex P1, PR #11). The masker existed and was unit
              // tested, but nothing called it, so a step that printed the secret
              // leaked it while the board claimed masking was done. This test
              // fails if the wiring is ever removed again.
              let root = tempRoot ()
              let binding = Secrets.bind root "TOKEN" "s3cr3t-value"
              let streamed = System.Collections.Generic.List<string>()

              let r =
                  Executor.runStep
                      { request root "echo \"leaking s3cr3t-value now\"" with
                          Secrets = [ binding ]
                          OnLine = Some(fun l -> streamed.Add l) }

              Expect.equal r.Status Success "step ran"

              let streamedText = String.Join("\n", streamed)

              Expect.isFalse (streamedText.Contains "s3cr3t-value") "STREAMED output must not carry the secret"
              Expect.isFalse (r.Stdout.Contains "s3cr3t-value") "BUFFERED output must not carry the secret"
              Expect.stringContains streamedText "leaking" "the rest of the line survives"
          }

          test "an encoding masking cannot cover is NAMED in the output" {
              // The FG-071 promise is not that nothing leaks — it is that a leak
              // is never silent. `rev` defeats the mask, so the engine must say so.
              let root = tempRoot ()
              let binding = Secrets.bind root "TOKEN" "s3cr3t-value"
              let streamed = System.Collections.Generic.List<string>()

              Executor.runStep
                  { request root "printf '%s\\n' \"$(echo s3cr3t-value | rev)\"" with
                      Secrets = [ binding ]
                      OnLine = Some(fun l -> streamed.Add l) }
              |> ignore

              let streamedText = String.Join("\n", streamed)

              Expect.stringContains streamedText "TOKEN" "the warning names the variable"
              Expect.stringContains streamedText "reversed" "the warning names the defeating encoding"
          }

          test "a transformed secret on STDERR is reported even with no OnLine callback" {
              // REVIEW FIX (Codex, PR #13): detection ran only inside the stdout
              // streaming callback, so this path returned the transformed secret
              // silently — and silence is the one thing FG-071 promises never to do.
              let root = tempRoot ()
              let binding = Secrets.bind root "TOKEN" "s3cr3t-value"

              let r =
                  Executor.runStep
                      { request root "echo s3cr3t-value | rev >&2" with
                          Secrets = [ binding ]
                          OnLine = None }

              Expect.stringContains r.Stderr "TOKEN" "the warning names the variable"
              Expect.stringContains r.Stderr "reversed" "and the defeating encoding"
          }

          test "no secrets configured means output is untouched" {
              // Guards against a masker that mangles ordinary builds.
              let r = Executor.runStep (request (tempRoot ()) "echo plain-output")
              Expect.stringContains r.Stdout "plain-output" "unchanged"
          } ]

[<EntryPoint>]
let main argv =
    // These tests spawn real processes and assert on /proc; running them in
    // parallel makes the survivor counts race against each other.
    runTestsWithCLIArgs
        []
        argv
        (testSequenced (testList "Fogell.Execution" [ workspaceHygiene; shellExecution; containment; secrets; deadProcessDetection; externalInterrupt; maskingOnOutputPath ]))
