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

          test "stderr is captured separately" {
              let r = Executor.runStep (request (tempRoot ()) "echo oops >&2")
              Expect.stringContains r.Stderr "oops" "stderr"
              Expect.isFalse (r.Stdout.Contains "oops") "not mixed into stdout"
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
                  Threading.Thread.Sleep 300
                  Expect.isFalse (pidAlive pid) $"daemon {pid} must be reaped after success"

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
                  Threading.Thread.Sleep 300
                  Expect.isFalse (pidAlive pid) $"daemon {pid} must be reaped on abort"
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
        [ test "BEAT JENKINS: the value is NOT in the child's environment" {
              // Measured: a secret in the environment is readable from
              // /proc/<pid>/environ by any process running as the same user, for
              // the whole life of the step. Jenkins' withCredentials does exactly
              // that. Fogell passes a PATH, never the value.
              let root = tempRoot ()
              let req = request root ""
              let binding = Secrets.bind req.Workspace "TOKEN" "SUPERSECRET123"

              let r =
                  Executor.runStep
                      { req with
                          // the script prints its OWN environment; if the value
                          // were there, it would appear here
                          Script = Some "cat /proc/self/environ | tr '\\0' '\\n' | sort"
                          Environment = Secrets.environmentFor [ binding ] }

              Expect.equal r.Status BuildStatus.Success "ran"
              Expect.isFalse (r.Stdout.Contains "SUPERSECRET123") "the value is absent from the environment"
              Expect.stringContains r.Stdout "TOKEN_FILE=" "only a path is exposed"
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

[<EntryPoint>]
let main argv =
    // These tests spawn real processes and assert on /proc; running them in
    // parallel makes the survivor counts race against each other.
    runTestsWithCLIArgs
        []
        argv
        (testSequenced (testList "Fogell.Execution" [ workspaceHygiene; shellExecution; containment; secrets; deadProcessDetection ]))
