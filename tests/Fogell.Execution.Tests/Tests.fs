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

let private runSecretUmaskChild reportFile =
    let root = tempRoot ()

    try
        try
            let binding = Secrets.bind root "TOKEN" "SUPERSECRET123"

            try
                let mode = File.GetUnixFileMode binding.FilePath
                let content = File.ReadAllText binding.FilePath
                File.WriteAllText(reportFile, $"{int mode}|{content}")

                if
                    mode = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                    && content = "SUPERSECRET123"
                then
                    0
                else
                    91
            finally
                Secrets.revoke [ binding ]
        with ex ->
            File.WriteAllText(reportFile, ex.ToString())
            92
    finally
        if Directory.Exists root then Directory.Delete(root, true)

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
      JUnitSkipMarkingBuildUnstable = false
      JUnitAllowEmptyResults = false
      JUnitSkipOldReportsSince = None
      JUnitSkipMarkingStageUnstable = false
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
let private shellQuote (value: string) = "'" + value.Replace("'", "'\"'\"'") + "'"

let private daemonScriptAfter (delayMilliseconds: int) (pidFile: string) =
    // The parent must not exit until the child has established the evidence
    // this test reads. The old 40ms reader-settlement floor accidentally gave
    // the child time to write; event-driven completion correctly lets reaping
    // win that race. Make the precondition explicit and bounded instead of
    // depending on executor slowness.
    let delay =
        if delayMilliseconds <= 0 then
            ""
        else
            let seconds =
                (float delayMilliseconds / 1_000.0)
                    .ToString("0.###", Globalization.CultureInfo.InvariantCulture)

            $"/bin/sleep {seconds}; "

    let quotedPidFile = shellQuote pidFile
    $"nohup /bin/sh -c '{delay}echo $$ > \"$1\"; exec /bin/sleep 600' fogell-daemon {quotedPidFile} >/dev/null 2>&1 & i=0; while [ ! -s {quotedPidFile} ]; do i=$((i+1)); [ \"$i\" -lt 300 ] || exit 97; /bin/sleep 0.01; done;"

let private daemonScript (pidFile: string) = daemonScriptAfter 0 pidFile

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

let private runContainmentChild registry pidFile readyFile =
    Environment.SetEnvironmentVariable("FOGELL_PROCESS_GROUP_REGISTRY", registry)

    let script =
        $"sleep 600 & fogell_effect=$!; printf '%%s' \"$fogell_effect\" > '{pidFile}'; printf ready > '{readyFile}'; wait \"$fogell_effect\""

    ProcessGroup.run
        { RunRequest.create (script, tempRoot ()) with
            ReapGroup = false }
    |> ignore

    0

let private runExitedLeaderContainmentChild registry pidFile readyFile =
    Environment.SetEnvironmentVariable("FOGELL_PROCESS_GROUP_REGISTRY", registry)

    let script =
        $"sleep 600 & fogell_effect=$!; printf '%%s' \"$fogell_effect\" > '{pidFile}'; printf ready > '{readyFile}'; exit 0"

    ProcessGroup.run
        { RunRequest.create (script, tempRoot ()) with
            ReapGroup = false }
    |> ignore
    0

let private runTermGraceContainmentChild registry pidFile termFile =
    Environment.SetEnvironmentVariable("FOGELL_PROCESS_GROUP_REGISTRY", registry)

    let script =
        $"/bin/sh -c 'trap \"\" TERM; exec /bin/sleep 600' & fogell_effect=$!; printf '%%s' \"$fogell_effect\" > '{pidFile}'; trap 'printf term > \"{termFile}\"' TERM; wait \"$fogell_effect\""

    ProcessGroup.run
        { RunRequest.create (script, tempRoot ()) with
            TimeoutMs = Some 200L
            GraceMs = 5_000 }
    |> ignore
    0

let private runHostilePathContainmentChild registry pidFile readyFile hostilePath =
    Environment.SetEnvironmentVariable("FOGELL_PROCESS_GROUP_REGISTRY", registry)

    let script =
        $"/bin/sh -c 'trap \"\" TERM; exec /bin/sleep 600' & fogell_effect=$!; /usr/bin/printf '%%s' \"$fogell_effect\" > '{pidFile}'; /usr/bin/printf ready > '{readyFile}'; wait \"$fogell_effect\""

    ProcessGroup.run
        { RunRequest.create (script, tempRoot ()) with
            Environment = [ "PATH", hostilePath ]
            ReapGroup = false }
    |> ignore
    0

let private runWatchdogRevalidationChild registry pidFile readyFile termFile killReleaseFile =
    Environment.SetEnvironmentVariable("FOGELL_PROCESS_GROUP_REGISTRY", registry)
    Environment.SetEnvironmentVariable("FOGELL_TEST_WATCHDOG_TERM_FILE", termFile)
    Environment.SetEnvironmentVariable("FOGELL_TEST_WATCHDOG_KILL_RELEASE_FILE", killReleaseFile)

    let script =
        $"/bin/sh -c 'trap \"\" TERM; exec /bin/sleep 600' & fogell_effect=$!; printf '%%s' \"$fogell_effect\" > '{pidFile}'; printf ready > '{readyFile}'; exit 0"

    ProcessGroup.run
        { RunRequest.create (script, tempRoot ()) with
            ReapGroup = false }
    |> ignore
    0

let private runZombieLeaderContainmentChild registry pidFile readyFile effectFile =
    Environment.SetEnvironmentVariable("FOGELL_PROCESS_GROUP_REGISTRY", registry)

    // pthread_exit leaves the process-group leader defunct while the Python
    // worker thread can still execute. This is the real `/proc/<pid>/stat`
    // shape whose field-20 expansion the generated POSIX watchdog must read.
    let python =
        "import ctypes,os,pathlib,sys,threading,time; "
        + "threading.Thread(target=lambda: (time.sleep(2), pathlib.Path(sys.argv[3]).write_text('escaped'))).start(); "
        + "pathlib.Path(sys.argv[1]).write_text(str(os.getpid())); "
        + "pathlib.Path(sys.argv[2]).write_text('ready'); "
        + "ctypes.CDLL(None).pthread_exit(None)"

    let script =
        $"exec /usr/bin/env python3 -c {shellQuote python} {shellQuote pidFile} {shellQuote readyFile} {shellQuote effectFile}"

    ProcessGroup.run
        { RunRequest.create (script, tempRoot ()) with
            ReapGroup = false }
    |> ignore
    0

let private runPreSetsidContainmentChild
    registry
    effectFile
    releaseFile
    readyFile
    observedFile
    stoppedFile
    cleanupReleaseFile
    =
    Environment.SetEnvironmentVariable("FOGELL_PROCESS_GROUP_REGISTRY", registry)
    Environment.SetEnvironmentVariable("FOGELL_TEST_PRE_SETSID_RELEASE_FILE", releaseFile)
    Environment.SetEnvironmentVariable("FOGELL_TEST_PRE_SETSID_READY_FILE", readyFile)
    Environment.SetEnvironmentVariable("FOGELL_TEST_PRE_SETSID_OBSERVED_FILE", observedFile)
    Environment.SetEnvironmentVariable("FOGELL_TEST_PRE_SETSID_STOPPED_FILE", stoppedFile)
    Environment.SetEnvironmentVariable("FOGELL_TEST_PRE_SETSID_CLEANUP_RELEASE_FILE", cleanupReleaseFile)

    ProcessGroup.run
        { RunRequest.create ($"printf ran > '{effectFile}'; sleep 600", tempRoot ()) with
            ReapGroup = false }
    |> ignore
    0

/// Alive per /proc, which needs no signal and cannot be confused by reuse
/// within a single test.
let private pidAlive (pid: int) = Directory.Exists $"/proc/{pid}"

let private containmentAnchorPids () =
    if not (OperatingSystem.IsLinux()) then
        Set.empty
    else
        Directory.GetDirectories "/proc"
        |> Array.choose (fun directory ->
            match Int32.TryParse(Path.GetFileName directory) with
            | true, pid ->
                try
                    let arguments =
                        File.ReadAllText(Path.Combine(directory, "cmdline"))
                            .Split('\000', StringSplitOptions.RemoveEmptyEntries)

                    // Match argv, not a substring: the outer wait wrapper has
                    // the anchor command in its shell-program argument and is
                    // deliberately alive during STOP-boundary tests.
                    if arguments.Length >= 2
                       && arguments.[0] = "/bin/sleep"
                       && arguments.[1] = "2147483647" then
                        Some pid
                    else
                        None
                with _ ->
                    None
            | _ -> None)
        |> Set.ofArray

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
        // Containment enables PR_SET_CHILD_SUBREAPER for production anchor
        // ownership. The test process therefore adopts orphaned fixtures too;
        // harvest only the exact PID this assertion already owns.
        Native.tryReapChild pid |> ignore
        Threading.Thread.Sleep 25

    Native.tryReapChild pid |> ignore
    not (pidAlive pid)

let private waitForFile path budgetMs =
    let clock = Diagnostics.Stopwatch.StartNew()

    while not (File.Exists path) && clock.ElapsedMilliseconds < int64 budgetMs do
        Threading.Thread.Sleep 10

    File.Exists path

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
          }

          test "materialization creates nested targets and is idempotent under contention" {
              let root = tempRoot ()
              let target = Path.Combine(root, "nested", "cwd")

              let results =
                  [ 1..16 ]
                  |> List.map (fun _ -> async { return Workspace.materializeUnder root target })
                  |> Async.Parallel
                  |> Async.RunSynchronously

              for result in results do
                  match result with
                  | Result.Ok() -> ()
                  | Result.Error e -> failtestf "concurrent materialization failed: %s" e.Describe

              Expect.isTrue (Directory.Exists target) "the exact nested cwd exists"
              Expect.isEmpty
                  (Directory.EnumerateFileSystemEntries(target) |> Seq.toList)
                  "materialization adds no payload"

              match Workspace.materializeUnder root root with
              | Result.Ok() -> ()
              | Result.Error e -> failtestf "the established attempt root was refused: %s" e.Describe
          }

          test "materialization refuses outside targets and existing symlink components" {
              let root = tempRoot ()
              let outside = tempRoot ()
              let sentinel = Path.Combine(outside, "sentinel.txt")
              File.WriteAllText(sentinel, "unchanged")

              match Workspace.materializeUnder root (Path.Combine(outside, "escaped")) with
              | Result.Error _ -> ()
              | Result.Ok() -> failtest "outside target was materialized"

              let finalLink = Path.Combine(root, "final-link")
              Directory.CreateSymbolicLink(finalLink, outside) |> ignore

              match Workspace.materializeUnder root finalLink with
              | Result.Error(Workspace.SymlinkComponent _) -> ()
              | other -> failtestf "expected final SymlinkComponent, got %A" other

              let parentLink = Path.Combine(root, "parent-link")
              Directory.CreateSymbolicLink(parentLink, outside) |> ignore

              match Workspace.materializeUnder root (Path.Combine(parentLink, "child")) with
              | Result.Error(Workspace.SymlinkComponent _) -> ()
              | other -> failtestf "expected parent SymlinkComponent, got %A" other

              Expect.isFalse (Directory.Exists(Path.Combine(outside, "escaped"))) "outside target was untouched"
              Expect.isFalse (Directory.Exists(Path.Combine(outside, "child"))) "symlink target was not followed"
              Expect.equal (File.ReadAllText sentinel) "unchanged" "outside sentinel bytes are unchanged"
          }

          test "materialization reports file collisions without throwing" {
              let root = tempRoot ()
              let fileTarget = Path.Combine(root, "file-target")
              File.WriteAllText(fileTarget, "payload")

              match Workspace.materializeUnder root fileTarget with
              | Result.Error(Workspace.MaterializationFailed _) -> ()
              | other -> failtestf "expected MaterializationFailed, got %A" other

              match Workspace.materializeUnder root (Path.Combine(fileTarget, "child")) with
              | Result.Error(Workspace.NonDirectoryParent _) -> ()
              | other -> failtestf "expected NonDirectoryParent, got %A" other

              Expect.equal (File.ReadAllText fileTarget) "payload" "colliding file bytes are unchanged"
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

let environmentIsolation =
    testList
        "FG-222 controller environment isolation"
        [ test "build launch replacement clears pre-seeded controller controls" {
              let psi = ProcessStartInfo("/bin/true")
              let planted =
                  [ "FOGELL_CREDENTIALS", "fg222-inline-control"
                    "DATABASE_URL", "postgres://controller/fg222"
                    "CONTROLLER_API_TOKEN", "fg222-api-control"
                    "SSH_AUTH_SOCK", "fg222-agent-control" ]

              for name, value in planted do
                  psi.Environment[name] <- value

              let requestedPath = "/fg222/request:/usr/bin:/bin"
              let neutralHome = "/tmp/fogell-agent-home"
              LaunchEnvironment.applyBuildTo
                  psi
                  [ "PATH", requestedPath
                    "HOME", neutralHome
                    "DECLARED", "pipeline-value" ]

              Expect.equal psi.Environment.Count 3 "no ambient or pre-seeded key survives replacement"
              Expect.equal psi.Environment["PATH"] requestedPath "explicit PATH remains"
              Expect.equal psi.Environment["HOME"] neutralHome "neutral build HOME remains"
              Expect.equal psi.Environment["DECLARED"] "pipeline-value" "declared value remains"

              for name, value in planted do
                  Expect.isFalse (psi.Environment.ContainsKey name) $"{name} is absent"
                  Expect.isFalse (psi.Environment.Values.Contains value) $"{name} value is absent"
          }

          test "a real shell receives only neutral metadata and declared input" {
              let neutralHome = IO.Path.Combine(IO.Path.GetTempPath(), "fogell-agent-home-test")
              let environment =
                  LaunchEnvironment.buildBaseline neutralHome
                  @ [ "DECLARED", "pipeline-value" ]

              Expect.equal
                  environment
                  [ "PATH", "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
                    "HOME", neutralHome
                    "DECLARED", "pipeline-value" ]
                  "the implicit baseline cannot widen without changing this contract"

              let result =
                  Executor.runStep
                      { request (tempRoot ()) "/usr/bin/env | /usr/bin/sort" with
                          Environment = environment }

              Expect.equal result.Status Success "the shell completed"
              Expect.stringContains result.Stdout "DECLARED=pipeline-value" "declared env is present"
              Expect.stringContains result.Stdout $"HOME={neutralHome}" "neutral HOME is present"
              Expect.stringContains result.Stdout "PATH=" "approved PATH is present"

              for name in [ "FOGELL_CREDENTIALS"; "FOGELL_CREDENTIALS_FILE"; "DATABASE_URL"; "CONTROLLER_API_TOKEN"; "SSH_AUTH_SOCK" ] do
                  Expect.isFalse (result.Stdout.Contains $"{name}=") $"{name} is absent from the child"
          }

          test "the production controller SCM snapshot copies only approved names" {
              let planted =
                  [ "SSH_AUTH_SOCK", "fg222-approved-agent"
                    "FG222_CUSTOM_SCM", "fg222-approved-custom"
                    "FOGELL_SCM_ENV_ALLOWLIST", "FG222_CUSTOM_SCM"
                    "CONTROLLER_API_TOKEN", "fg222-denied-api"
                    "AWS_SECRET_ACCESS_KEY", "fg222-denied-aws" ]

              let previous =
                  planted |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable name)

              try
                  for name, value in planted do
                      Environment.SetEnvironmentVariable(name, value)

                  let psi = ProcessStartInfo("/bin/true")
                  LaunchEnvironment.applyControllerScmTo psi (LaunchEnvironment.controllerScmBaseline ())

                  let allowed =
                      set
                          [ "PATH"; "HOME"; "USER"; "LOGNAME"; "XDG_CONFIG_HOME"
                            "SSH_AUTH_SOCK"; "SSH_ASKPASS"; "GIT_ASKPASS"; "GIT_SSH"; "GIT_SSH_COMMAND"
                            "GIT_CONFIG_GLOBAL"; "GIT_CONFIG_SYSTEM"; "GIT_SSL_CAINFO"
                            "SSL_CERT_FILE"; "SSL_CERT_DIR"
                            "HTTP_PROXY"; "HTTPS_PROXY"; "ALL_PROXY"; "NO_PROXY"
                            "http_proxy"; "https_proxy"; "all_proxy"; "no_proxy"
                            "FG222_CUSTOM_SCM" ]

                  for key in psi.Environment.Keys do
                      Expect.isTrue (allowed.Contains key) $"production SCM profile contains only approved key {key}"

                  Expect.equal psi.Environment["SSH_AUTH_SOCK"] "fg222-approved-agent" "standard SCM authority is present"
                  Expect.equal psi.Environment["FG222_CUSTOM_SCM"] "fg222-approved-custom" "opted-in authority is present"
                  Expect.isFalse (psi.Environment.ContainsKey "FOGELL_SCM_ENV_ALLOWLIST") "the selector is not copied"
                  Expect.isFalse (psi.Environment.ContainsKey "CONTROLLER_API_TOKEN") "unapproved API control is absent"
                  Expect.isFalse (psi.Environment.ContainsKey "AWS_SECRET_ACCESS_KEY") "unapproved cloud control is absent"
              finally
                  for name, value in previous do
                      Environment.SetEnvironmentVariable(name, value)
          }

          test "Git resolution obeys relative empty executable and missing PATH entries" {
              let root = IO.Path.Combine(IO.Path.GetTempPath(), "fogell-fg222-path-" + Guid.NewGuid().ToString("N"))
              let tools = IO.Path.Combine(root, "tools")
              let blocked = IO.Path.Combine(root, "blocked")
              IO.Directory.CreateDirectory tools |> ignore
              IO.Directory.CreateDirectory blocked |> ignore

              let executable = IO.Path.Combine(tools, "git")
              let currentExecutable = IO.Path.Combine(root, "git")
              let nonExecutable = IO.Path.Combine(blocked, "git")

              try
                  for path in [ executable; currentExecutable; nonExecutable ] do
                      IO.File.WriteAllText(path, "#!/bin/sh\nexit 0\n")

                  let executableMode =
                      IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserWrite ||| IO.UnixFileMode.UserExecute

                  IO.File.SetUnixFileMode(executable, executableMode)
                  IO.File.SetUnixFileMode(currentExecutable, executableMode)
                  IO.File.SetUnixFileMode(nonExecutable, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserWrite)

                  Expect.equal
                      (LaunchEnvironment.resolveBuildExecutable "git" root [ "PATH", "blocked:tools" ])
                      (Some executable)
                      "relative PATH is resolved from the child cwd and skips a non-executable candidate"

                  Expect.equal
                      (LaunchEnvironment.resolveBuildExecutable "git" root [ "PATH", "" ])
                      (Some currentExecutable)
                      "an empty PATH entry names the child cwd"

                  Expect.isNone
                      (LaunchEnvironment.resolveBuildExecutable "git" root [ "PATH", IO.Path.Combine(root, "missing") ])
                      "an unusable explicit PATH fails closed instead of falling back to controller PATH"
              finally
                  try IO.Directory.Delete(root, true) with _ -> ()
          } ]

let containment =
    testList
        "FG-031/032 process group containment"
        [ test "a running registered leader is released only after its stopped state is observed" {
              let states =
                  Collections.Generic.Queue(
                      [ ProcessGroup.ProcessIdentityState.Matching 'R'
                        ProcessGroup.ProcessIdentityState.Matching 'S'
                        ProcessGroup.ProcessIdentityState.Matching 'T' ])
              let mutable pauses = 0

              let stopped =
                  ProcessGroup.waitForIdentityStop
                      3
                      (fun () -> states.Dequeue())
                      (fun () -> pauses <- pauses + 1)

              Expect.isTrue stopped "SIGCONT is authorized only by the observed stopped state"
              Expect.equal pauses 2 "running states consume the bounded polling budget"

              let launcherStates =
                  Collections.Generic.Queue(
                      [ ProcessGroup.LauncherFormationState.SameIdentity 17
                        ProcessGroup.LauncherFormationState.SameIdentity 42 ])

              let formed =
                  ProcessGroup.waitForIdentityGroup
                      42
                      1
                      (fun () -> launcherStates.Dequeue())
                      ignore

              Expect.equal
                  formed
                  ProcessGroup.LauncherFormationDecision.GroupFormed
                  "the EOF guard follows the same launcher identity across setsid"

              let timedOut =
                  ProcessGroup.waitForIdentityGroup
                      42
                      0
                      (fun () -> ProcessGroup.LauncherFormationState.SameIdentity 17)
                      ignore

              Expect.equal
                  timedOut
                  ProcessGroup.LauncherFormationDecision.TerminateLauncher
                  "the bound kills a still-pre-setsid launcher instead of abandoning it"

              let drifted =
                  ProcessGroup.waitForIdentityGroup
                      42
                      20
                      (fun () -> ProcessGroup.LauncherFormationState.Changed)
                      ignore

              Expect.equal
                  drifted
                  ProcessGroup.LauncherFormationDecision.Refused
                  "numeric pid reuse never authorizes either group or launcher signalling"

              let disappeared =
                  ProcessGroup.waitForIdentityGroup
                      42
                      20
                      (fun () -> ProcessGroup.LauncherFormationState.Absent)
                      ignore

              Expect.equal
                  disappeared
                  ProcessGroup.LauncherFormationDecision.Disappeared
                  "a launcher that vanished before session formation needs no signal"
          }

          test "a leader that never stops is refused at the exact polling bound" {
              let mutable observations = 0
              let mutable pauses = 0

              let stopped =
                  ProcessGroup.waitForIdentityStop
                      2
                      (fun () ->
                          observations <- observations + 1
                          ProcessGroup.ProcessIdentityState.Matching 'R')
                      (fun () -> pauses <- pauses + 1)

              Expect.isFalse stopped "a marker alone can never authorize SIGCONT"
              Expect.equal observations 3 "the initial observation plus two bounded retries were made"
              Expect.equal pauses 2 "the timeout has no hidden extra sleep"
          }

          test "registered cleanup binds every signal and ignores only defunct group members" {
              Expect.equal
                  (Native.classifyProcessGroupQuery 42 0)
                  (Native.ProcessGroupQuery.Found 42)
                  "getpgid success yields the candidate group"
              Expect.equal
                  (Native.classifyProcessGroupQuery -1 Native.ESRCH)
                  Native.ProcessGroupQuery.Absent
                  "a vanished candidate is not an uncertain host-wide blocker"
              Expect.equal
                  (Native.classifyProcessGroupQuery -1 1)
                  Native.ProcessGroupQuery.Uncertain
                  "permission or kernel uncertainty remains fail-closed"

              let fields = Array.create 20 "0"
              fields[0] <- "S"
              fields[1] <- "7"
              fields[2] <- "42"
              fields[17] <- "1"
              fields[19] <- "9001"
              let parsed =
                  ProcessGroup.parseLinuxProcessStat(
                      "123 (a command ) with parens) " + String.concat " " fields)
                  |> Option.defaultWith (fun () -> failtest "valid proc stat did not parse")

              Expect.equal parsed.State 'S' "state is parsed after the final comm parenthesis"
              Expect.equal parsed.ParentProcessId 7 "ppid uses proc stat field 4"
              Expect.equal parsed.ProcessGroupId 42 "pgrp uses proc stat field 5"
              Expect.equal parsed.ThreadCount 1 "thread count uses proc stat field 20"
              Expect.equal parsed.StartTime "9001" "start ticks use proc stat field 22"

              let stat state group threads start =
                  ProcessGroup.LinuxProcessObservation.Present
                      { State = state
                        ParentProcessId = Environment.ProcessId
                        ProcessGroupId = group
                        ThreadCount = threads
                        StartTime = start }

              let mutable candidateReads = []
              let observe pid =
                  candidateReads <- pid :: candidateReads
                  stat 'S' 7 1 "replacement"

              Expect.equal
                  (ProcessGroup.observeLiveGroupCandidate
                      42
                      123
                      (fun _ -> Native.ProcessGroupQuery.Found 42)
                      observe)
                  (Some(123, ProcessGroup.LinuxProcessObservation.Uncertain))
                  "a PID whose group changes between getpgid and stat is fail-closed"
              Expect.equal candidateReads [ 123 ] "the matched candidate was read exactly once"

              candidateReads <- []
              let mutable movingQueries =
                  [ Native.ProcessGroupQuery.Found 7
                    Native.ProcessGroupQuery.Found 42 ]
              let movingQuery _ =
                  match movingQueries with
                  | next :: rest ->
                      movingQueries <- rest
                      next
                  | [] -> failtest "foreign candidate was queried more than twice"

              Expect.equal
                  (ProcessGroup.scanLiveGroupCandidates
                      42
                      [ 124 ]
                      movingQuery
                      (fun pid ->
                          candidateReads <- pid :: candidateReads
                          stat 'S' 42 1 "joined"))
                  [ 124, stat 'S' 42 1 "joined" ]
                  "a process joining the group during the scan is retained"
              Expect.equal candidateReads [ 124 ] "the newly joined candidate was read exactly once"

              candidateReads <- []
              let mutable stableForeignQueries = 0
              let stableForeign _ =
                  stableForeignQueries <- stableForeignQueries + 1
                  Native.ProcessGroupQuery.Found 7

              Expect.isEmpty
                  (ProcessGroup.scanLiveGroupCandidates 42 [ 125 ] stableForeign observe)
                  "a process outside the group at both observations is ignored"
              Expect.equal stableForeignQueries 2 "stable foreign candidates are checked at the boundary"
              Expect.isEmpty candidateReads "stable foreign candidates still avoid proc stat reads"

              Expect.equal
                  (ProcessGroup.classifyLiveGroupMembers
                      42
                      None
                      [ 1, stat 'Z' 42 1 "1"; 2, stat 'X' 42 1 "2" ])
                  0
                  "zombie and dead remnants are logically extinct"
              Expect.equal
                  (ProcessGroup.classifyLiveGroupMembers
                      42
                      None
                      [ 1, stat 'Z' 42 1 "1"; 2, stat 'D' 42 1 "2" ])
                  1
                  "an uninterruptible live member remains a survivor"
              Expect.equal
                  (ProcessGroup.classifyLiveGroupMembers
                      42
                      None
                      [ 1, stat 'Z' 42 2 "1" ])
                  1
                  "a defunct leader with sibling threads remains live"
              Expect.equal
                  (ProcessGroup.classifyLiveGroupMembers
                      42
                      (Some(2, "2"))
                      [ 1, stat 'Z' 42 1 "1"; 2, stat 'S' 42 1 "2" ])
                  0
                  "the explicit anchor exclusion removes only its recorded birth identity"
              Expect.equal
                  (ProcessGroup.classifyLiveGroupMembers
                      42
                      (Some(2, "2"))
                      [ 1, stat 'Z' 42 1 "1"; 2, stat 'S' 42 1 "replacement" ])
                  1
                  "a reused anchor PID in the target group remains a live survivor"

              let mutable reapCalls = 0
              let reap observation expectedStart =
                  ProcessGroup.reapObservedRegisteredMember
                      42
                      43
                      expectedStart
                      Environment.ProcessId
                      (fun () -> observation)
                      (fun () -> reapCalls <- reapCalls + 1)

              Expect.isTrue
                  (reap (stat 'Z' 42 1 "anchor") (Some "anchor"))
                  "the exact inert registered anchor is eligible for PID-specific waitpid"
              Expect.equal reapCalls 1 "the exact anchor is reaped once"
              Expect.isFalse
                  (reap (stat 'Z' 42 1 "replacement") (Some "anchor"))
                  "a reused anchor PID is not harvested"
              Expect.isFalse
                  (reap (stat 'Z' 7 1 "anchor") (Some "anchor"))
                  "a process that moved to another group is not harvested"
              Expect.isFalse
                  (reap (stat 'S' 42 1 "anchor") (Some "anchor"))
                  "a live group member is never passed to nonblocking waitpid"
              let shellOwned =
                  ProcessGroup.LinuxProcessObservation.Present
                      { State = 'Z'
                        ParentProcessId = Environment.ProcessId + 1
                        ProcessGroupId = 42
                        ThreadCount = 1
                        StartTime = "anchor" }
              Expect.isFalse
                  (reap shellOwned (Some "anchor"))
                  "a wrapper-shell-owned zombie is not harvested before adoption"
              Expect.isTrue
                  (ProcessGroup.reapObservedRegisteredMember
                      42
                      42
                      None
                      Environment.ProcessId
                      (fun () -> stat 'Z' 42 1 "leader")
                      (fun () -> reapCalls <- reapCalls + 1))
                  "an adopted registered leader is harvested once its wrapper owner is gone"
              Expect.equal reapCalls 2 "adopted inert members reap; every identity, ownership, membership, and state mismatch withholds waitpid"
              Expect.equal
                  (ProcessGroup.classifyLiveGroupMembers
                      42
                      None
                      [ 1, ProcessGroup.LinuxProcessObservation.Uncertain ])
                  -1
                  "an unreadable proc entry cannot become a clean zero"

              let identity: ProcessGroup.RegisteredGroupIdentity =
                  { GroupId = 42
                    LeaderStartTime = "leader"
                    AnchorPid = 84
                    AnchorStartTime = "anchor" }
              let absent () = ProcessGroup.LinuxProcessObservation.Absent
              let mutable anchor = stat 'T' 42 1 "anchor"
              let mutable groupSignals = []

              let sendGroup signal =
                  groupSignals <- groupSignals @ [ signal ]
                  anchor <- stat 'T' 42 1 "replacement"
                  true

              Expect.isTrue
                  (ProcessGroup.signalRegisteredGroup identity absent (fun () -> anchor) sendGroup Native.SIGTERM)
                  "a fresh matching anchor authorizes TERM"
              Expect.isFalse
                  (ProcessGroup.signalRegisteredGroup identity absent (fun () -> anchor) sendGroup Native.SIGKILL)
                  "start-tick drift before KILL revokes authority"
              Expect.equal groupSignals [ Native.SIGTERM ] "the replacement group never receives KILL"

              let mutable waitedAfterKill = false

              Expect.isFalse
                  (ProcessGroup.attemptEscalation
                      false
                      (fun () -> false)
                      (fun () -> waitedAfterKill <- true))
                  "withheld KILL is not reported as an escalation"
              Expect.isFalse waitedAfterKill "withheld KILL has no post-signal wait"

              Expect.isTrue
                  (ProcessGroup.attemptEscalation
                      false
                      (fun () -> true)
                      (fun () -> waitedAfterKill <- true))
                  "delivered KILL is reported as an escalation"
              Expect.isTrue waitedAfterKill "delivered KILL runs the post-signal wait"

              Expect.isTrue
                  (ProcessGroup.deliverOrObserveExtinction (fun () -> false) (fun () -> true))
                  "an ESRCH-shaped delivery race is clean only after fresh extinction proof"
              Expect.isFalse
                  (ProcessGroup.deliverOrObserveExtinction (fun () -> false) (fun () -> false))
                  "failed delivery with live or uncertain survivors remains fail-closed"

              Expect.equal
                  ProcessGroup.preCaptureFallbackScript
                  "wait \"$fogell_inner\" 2>/dev/null || true; exit 125; "
                  "missing birth identity refuses without any numeric signal"
              Expect.isFalse
                  (ProcessGroup.preCaptureFallbackScript.Contains "/bin/kill")
                  "a capture failure cannot signal a reused PID or group"

              let mutable anchorSignals = []
              let mutable directAnchor = stat 'T' 42 1 "anchor"

              let sendAnchor signal =
                  anchorSignals <- anchorSignals @ [ signal ]
                  directAnchor <- stat 'T' 42 1 "replacement"
                  true

              Expect.isTrue
                  (ProcessGroup.signalRegisteredAnchor identity (fun () -> directAnchor) sendAnchor Native.SIGTERM)
                  "the exact anchor receives pending TERM"
              Expect.isFalse
                  (ProcessGroup.signalRegisteredAnchor identity (fun () -> directAnchor) sendAnchor Native.SIGCONT)
                  "the same numeric PID with changed birth ticks cannot receive CONT"
              Expect.equal anchorSignals [ Native.SIGTERM ] "anchor identity is checked separately for every signal"
          }

          test "leader identity drift refuses release immediately" {
              let states =
                  Collections.Generic.Queue(
                      [ ProcessGroup.ProcessIdentityState.Matching 'R'
                        ProcessGroup.ProcessIdentityState.Changed ])
              let mutable pauses = 0

              let stopped =
                  ProcessGroup.waitForIdentityStop
                      20
                      (fun () -> states.Dequeue())
                      (fun () -> pauses <- pauses + 1)

              Expect.isFalse stopped "a reused numeric pid cannot authorize SIGCONT"
              Expect.equal pauses 1 "identity drift fails closed without spending the remaining budget"
          }

          test "pre-registration owner failures never release or leak the launcher" {
              let root = tempRoot ()
              let invalidRegistry = Path.Combine(root, "registry-is-a-file")
              let userEffect = Path.Combine(root, "user-effect")
              let previousRegistry = Environment.GetEnvironmentVariable "FOGELL_PROCESS_GROUP_REGISTRY"
              let anchorsBefore = containmentAnchorPids ()
              File.WriteAllText(invalidRegistry, "not a directory")
              Environment.SetEnvironmentVariable("FOGELL_PROCESS_GROUP_REGISTRY", invalidRegistry)

              try
                  Expect.throws
                      (fun () ->
                          ProcessGroup.run
                              (RunRequest.create ($"printf ran > '{userEffect}'", root))
                          |> ignore)
                      "registration failure is surfaced rather than releasing unregistered code"
              finally
                  Environment.SetEnvironmentVariable("FOGELL_PROCESS_GROUP_REGISTRY", previousRegistry)

              let clock = Stopwatch.StartNew()
              let mutable leaked = Set.difference (containmentAnchorPids ()) anchorsBefore

              while not leaked.IsEmpty && clock.ElapsedMilliseconds < 3_000L do
                  Threading.Thread.Sleep 25
                  leaked <- Set.difference (containmentAnchorPids ()) anchorsBefore

              Expect.isFalse (File.Exists userEffect) "the stopped user leader never executed its command"
              Expect.isEmpty leaked "the no-record guard reaped the anchor under the bounded fallback"

              // Exercise the still-earlier window: the owner dies while the
              // direct child has been forked but has not executed setsid or
              // emitted its marker. A release-file gate makes the boundary
              // exact; the guard acknowledgement proves EOF was observed while
              // pid and pgrp were still different.
              let earlyRoot = tempRoot ()
              let registry = Path.Combine(earlyRoot, "registry")
              let effect = Path.Combine(earlyRoot, "effect")
              let release = Path.Combine(earlyRoot, "release")
              let ready = Path.Combine(earlyRoot, "launcher.pid")
              let observed = Path.Combine(earlyRoot, "guard.observed")
              let earlyAnchorsBefore = containmentAnchorPids ()
              Directory.CreateDirectory registry |> ignore

              let start = ProcessStartInfo(Environment.ProcessPath)

              if Path.GetFileNameWithoutExtension(Environment.ProcessPath) = "dotnet" then
                  start.ArgumentList.Add(Reflection.Assembly.GetExecutingAssembly().Location)

              start.ArgumentList.Add "--containment-pre-setsid-child"
              start.ArgumentList.Add registry
              start.ArgumentList.Add effect
              start.ArgumentList.Add release
              start.ArgumentList.Add ready
              start.ArgumentList.Add observed
              start.ArgumentList.Add ""
              start.ArgumentList.Add ""
              start.UseShellExecute <- false
              start.RedirectStandardOutput <- true
              start.RedirectStandardError <- true
              use host = Process.Start start

              if not (waitForFile ready 5_000) then
                  host.Kill(true)
                  failtest "pre-setsid launcher did not establish the deterministic gate"

              let launcherPid = Int32.Parse((File.ReadAllText ready).Trim())
              Expect.isTrue (pidAlive launcherPid) "the delayed launcher was alive before owner death"
              Expect.notEqual
                  (Native.processGroupOf launcherPid)
                  (Some launcherPid)
                  "the test kills the owner before setsid, not after group formation"
              Expect.isTrue
                  (Directory.EnumerateFiles(registry, "*.group") |> Seq.isEmpty)
                  "no marker or durable registration existed at the failure boundary"

              host.Kill()
              host.WaitForExit 3_000 |> ignore

              if not (waitForFile observed 3_000) then
                  File.WriteAllText(release, "release")
                  failtest "EOF guard did not observe the identity-bound pre-setsid launcher"

              File.WriteAllText(release, "release")
              Expect.isTrue (waitForReap launcherPid) "the guard followed setsid and reaped the formed group"
              Expect.isFalse (File.Exists effect) "user code remained gated across abrupt owner death"

              let anchorClock = Stopwatch.StartNew()
              let mutable earlyLeaked = Set.difference (containmentAnchorPids ()) earlyAnchorsBefore

              while not earlyLeaked.IsEmpty && anchorClock.ElapsedMilliseconds < 3_000L do
                  Threading.Thread.Sleep 25
                  earlyLeaked <- Set.difference (containmentAnchorPids ()) earlyAnchorsBefore

              Expect.isEmpty earlyLeaked "the earliest owner-death window left no identity anchor"
              Expect.isTrue
                  (Directory.EnumerateFiles(registry, "*.group") |> Seq.isEmpty)
                  "the pre-marker failure did not manufacture registration evidence"
              Directory.Delete(earlyRoot, true)

              // The polling bound is itself fail-closed. If setsid has not yet
              // arrived, the guard freezes and identity-checks the direct
              // launcher before classifying it. The test then releases the
              // launch gate while cleanup is deliberately paused: acknowledged
              // T/t, not signal delivery alone, is what prevents transition.
              let timeoutRoot = tempRoot ()
              let timeoutRegistry = Path.Combine(timeoutRoot, "registry")
              let timeoutEffect = Path.Combine(timeoutRoot, "effect")
              let timeoutRelease = Path.Combine(timeoutRoot, "never-release")
              let timeoutReady = Path.Combine(timeoutRoot, "launcher.pid")
              let timeoutObserved = Path.Combine(timeoutRoot, "guard.observed")
              let timeoutStopped = Path.Combine(timeoutRoot, "guard.stopped")
              let timeoutCleanupRelease = Path.Combine(timeoutRoot, "cleanup.release")
              Directory.CreateDirectory timeoutRegistry |> ignore

              let timeoutStart = ProcessStartInfo(Environment.ProcessPath)

              if Path.GetFileNameWithoutExtension(Environment.ProcessPath) = "dotnet" then
                  timeoutStart.ArgumentList.Add(Reflection.Assembly.GetExecutingAssembly().Location)

              timeoutStart.ArgumentList.Add "--containment-pre-setsid-child"
              timeoutStart.ArgumentList.Add timeoutRegistry
              timeoutStart.ArgumentList.Add timeoutEffect
              timeoutStart.ArgumentList.Add timeoutRelease
              timeoutStart.ArgumentList.Add timeoutReady
              timeoutStart.ArgumentList.Add timeoutObserved
              timeoutStart.ArgumentList.Add timeoutStopped
              timeoutStart.ArgumentList.Add timeoutCleanupRelease
              timeoutStart.UseShellExecute <- false
              timeoutStart.RedirectStandardOutput <- true
              timeoutStart.RedirectStandardError <- true
              use timeoutHost = Process.Start timeoutStart

              if not (waitForFile timeoutReady 5_000) then
                  timeoutHost.Kill(true)
                  failtest "bounded pre-setsid launcher did not establish its gate"

              let timeoutLauncher = Int32.Parse((File.ReadAllText timeoutReady).Trim())
              timeoutHost.Kill()
              timeoutHost.WaitForExit 3_000 |> ignore
              Expect.isTrue
                  (waitForFile timeoutObserved 3_000)
                  "the timeout case non-vacuously entered pre-setsid polling"
              Expect.isTrue
                  (waitForFile timeoutStopped 3_000)
                  "the guard acknowledged the exact launcher identity in T/t before classifying it"
              let frozenPgrp = Int32.Parse((File.ReadAllText timeoutStopped).Trim())
              Expect.isGreaterThan
                  frozenPgrp
                  1
                  (if frozenPgrp = timeoutLauncher then
                       "STOP acknowledgement classified an already-formed inner group"
                   else
                       "STOP acknowledgement classified the direct pre-setsid launcher")
              File.WriteAllText(timeoutRelease, "release")
              Threading.Thread.Sleep 50
              Expect.isTrue (pidAlive timeoutLauncher) "cleanup remained paused with the launcher frozen"
              Expect.equal
                  (Native.processGroupOf timeoutLauncher)
                  (Some frozenPgrp)
                  "the acknowledged frozen classification remained stable after gate release"
              Expect.isFalse (File.Exists timeoutEffect) "a released gate cannot advance a stopped launcher"
              File.WriteAllText(timeoutCleanupRelease, "cleanup")
              Expect.isTrue
                  (waitForReap timeoutLauncher)
                  "the polling bound killed the frozen same-identity launcher"
              Expect.isFalse (File.Exists timeoutEffect) "timeout never released user code"

              let timeoutAnchorClock = Stopwatch.StartNew()
              let mutable timeoutAnchors = Set.difference (containmentAnchorPids ()) earlyAnchorsBefore

              while not timeoutAnchors.IsEmpty && timeoutAnchorClock.ElapsedMilliseconds < 3_000L do
                  Threading.Thread.Sleep 25
                  timeoutAnchors <- Set.difference (containmentAnchorPids ()) earlyAnchorsBefore

              Expect.isEmpty
                  timeoutAnchors
                  $"cleanup reaped every anchor after frozen pgrp {frozenPgrp} was classified"
              Expect.isTrue
                  (Directory.EnumerateFiles(timeoutRegistry, "*.group") |> Seq.isEmpty)
                  "timeout before setsid created no false registration"
              Directory.Delete(timeoutRoot, true)
          }

          test "an abrupt Run.Host death reaps a registered inner step group" {
              let root = tempRoot ()
              let registry = Path.Combine(root, "registry")
              let pidFile = Path.Combine(root, "effect.pid")
              let readyFile = Path.Combine(root, "effect.ready")
              Directory.CreateDirectory registry |> ignore

              let start = ProcessStartInfo(Environment.ProcessPath)

              if Path.GetFileNameWithoutExtension(Environment.ProcessPath) = "dotnet" then
                  start.ArgumentList.Add(Reflection.Assembly.GetExecutingAssembly().Location)

              start.ArgumentList.Add "--containment-child"
              start.ArgumentList.Add registry
              start.ArgumentList.Add pidFile
              start.ArgumentList.Add readyFile
              start.UseShellExecute <- false
              start.RedirectStandardOutput <- true
              start.RedirectStandardError <- true
              use host = Process.Start start

              let clock = Stopwatch.StartNew()

              while ((not (File.Exists readyFile)
                      || (Directory.EnumerateFiles(registry, "*.group") |> Seq.isEmpty))
                     && clock.ElapsedMilliseconds < 5_000L) do
                  Threading.Thread.Sleep 25

              let effectPid =
                  match waitForPidFile pidFile with
                  | Some pid -> pid
                  | None ->
                      host.Kill(true)
                      failtest "inner effect never established its non-vacuous pid precondition"

              let record = Directory.EnumerateFiles(registry, "*.group") |> Seq.exactlyOne
              let recordFields =
                  File.ReadAllText(record).Split(' ', StringSplitOptions.RemoveEmptyEntries)
              Expect.equal recordFields.Length 4 "the registry binds both leader and identity anchor"
              let anchorPid = Int32.Parse recordFields.[2]

              Expect.isTrue (pidAlive effectPid) "the background effect was alive before abrupt host death"
              Expect.isTrue (pidAlive anchorPid) "the anchor continuously reserved the inner PGID"
              Expect.isFalse
                  (Directory.EnumerateFiles(registry, "*.group") |> Seq.isEmpty)
                  "the inner group was durably registered before effects ran"

              host.Kill()
              host.WaitForExit 3_000 |> ignore
              Expect.isTrue (waitForReap effectPid) "stdin-EOF watchdog reaped the inner group after SIGKILL"
              Expect.isTrue (waitForReap anchorPid) "the watchdog reaped its continuous identity anchor"
              Expect.isTrue
                  (File.Exists record)
                  "the watchdog leaves durable cleanup evidence for the controller verifier"
              Directory.Delete(root, true)
          }

          test "EOF watchdog cleanup cannot resolve its grace sleep from hostile PATH" {
              if not (OperatingSystem.IsLinux()) then
                  Tests.skiptest "the watchdog containment contract is Linux-only"

              let root = tempRoot ()
              let registry = Path.Combine(root, "registry")
              let hostilePath = Path.Combine(root, "hostile-path")
              let pidFile = Path.Combine(root, "effect.pid")
              let readyFile = Path.Combine(root, "effect.ready")
              let intercepted = Path.Combine(root, "sleep.intercepted")
              let hostileSleep = Path.Combine(hostilePath, "sleep")
              Directory.CreateDirectory registry |> ignore
              Directory.CreateDirectory hostilePath |> ignore
              File.WriteAllText(
                  hostileSleep,
                  $"#!/bin/sh\n/usr/bin/printf intercepted > '{intercepted}'\nexec /bin/sleep 30\n")
              File.SetUnixFileMode(
                  hostileSleep,
                  UnixFileMode.UserRead
                  ||| UnixFileMode.UserWrite
                  ||| UnixFileMode.UserExecute)

              let start = ProcessStartInfo(Environment.ProcessPath)

              if Path.GetFileNameWithoutExtension(Environment.ProcessPath) = "dotnet" then
                  start.ArgumentList.Add(Reflection.Assembly.GetExecutingAssembly().Location)

              start.ArgumentList.Add "--containment-hostile-path-child"
              start.ArgumentList.Add registry
              start.ArgumentList.Add pidFile
              start.ArgumentList.Add readyFile
              start.ArgumentList.Add hostilePath
              start.UseShellExecute <- false
              start.RedirectStandardOutput <- true
              start.RedirectStandardError <- true
              use host = Process.Start start
              let mutable group = 0

              try
                  let clock = Stopwatch.StartNew()

                  while ((not (File.Exists readyFile)
                          || (Directory.EnumerateFiles(registry, "*.group") |> Seq.isEmpty))
                         && clock.ElapsedMilliseconds < 5_000L) do
                      Threading.Thread.Sleep 10

                  let effectPid =
                      match waitForPidFile pidFile with
                      | Some pid -> pid
                      | None -> failtest "hostile-PATH effect never established its non-vacuous pid precondition"

                  let record = Directory.EnumerateFiles(registry, "*.group") |> Seq.exactlyOne
                  let fields = File.ReadAllText(record).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  group <- Int32.Parse fields.[0]
                  Expect.isTrue (pidAlive effectPid) "the TERM-resistant effect was alive before owner death"

                  host.Kill()
                  host.WaitForExit 3_000 |> ignore
                  Expect.isTrue (waitForReap effectPid) "the EOF watchdog reached absolute-path KILL"
                  Expect.isFalse (File.Exists intercepted) "the build-controlled sleep executable was never invoked"
              finally
                  if not host.HasExited then
                      host.Kill(true)

                  if group > 1 then
                      Native.signalGroup group Native.SIGKILL |> ignore

                  Directory.Delete(root, true)
          }

          test "EOF watchdog authorizes a zombie leader that still has live threads" {
              if not (OperatingSystem.IsLinux()) then
                  Tests.skiptest "the multi-threaded zombie watchdog regression is Linux-only"

              let root = tempRoot ()
              let registry = Path.Combine(root, "registry")
              let pidFile = Path.Combine(root, "leader.pid")
              let readyFile = Path.Combine(root, "leader.ready")
              let effectFile = Path.Combine(root, "escaped.effect")
              Directory.CreateDirectory registry |> ignore

              let start = ProcessStartInfo(Environment.ProcessPath)

              if Path.GetFileNameWithoutExtension(Environment.ProcessPath) = "dotnet" then
                  start.ArgumentList.Add(Reflection.Assembly.GetExecutingAssembly().Location)

              start.ArgumentList.Add "--containment-zombie-leader-child"
              start.ArgumentList.Add registry
              start.ArgumentList.Add pidFile
              start.ArgumentList.Add readyFile
              start.ArgumentList.Add effectFile
              start.UseShellExecute <- false
              start.RedirectStandardOutput <- true
              start.RedirectStandardError <- true
              use host = Process.Start start
              let mutable group = 0
              let mutable anchor = 0

              let readStat pid =
                  try
                      File.ReadAllText($"/proc/{pid}/stat")
                      |> ProcessGroup.parseLinuxProcessStat
                  with _ ->
                      None

              try
                  let registrationClock = Stopwatch.StartNew()

                  while ((not (File.Exists readyFile)
                          || (Directory.EnumerateFiles(registry, "*.group") |> Seq.isEmpty))
                         && registrationClock.ElapsedMilliseconds < 5_000L) do
                      Threading.Thread.Sleep 10

                  let leader =
                      match waitForPidFile pidFile with
                      | Some pid -> pid
                      | None -> failtest "the threaded leader did not publish its pid"

                  let record = Directory.EnumerateFiles(registry, "*.group") |> Seq.exactlyOne
                  let fields = File.ReadAllText(record).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  group <- Int32.Parse fields.[0]
                  anchor <- Int32.Parse fields.[2]
                  Expect.equal leader group "the Python thread-group leader owns the registered process group"

                  let zombieClock = Stopwatch.StartNew()
                  let mutable leaderStat = readStat leader

                  while
                      (leaderStat
                       |> Option.exists (fun stat -> stat.State = 'Z' && stat.ThreadCount > 1)
                       |> not)
                      && zombieClock.ElapsedMilliseconds < 3_000L
                      do
                      Threading.Thread.Sleep 10
                      leaderStat <- readStat leader

                  match leaderStat with
                  | Some stat ->
                      Expect.equal stat.State 'Z' "the registered leader is genuinely defunct"
                      Expect.isGreaterThan stat.ThreadCount 1 "a sibling thread can still execute effects"
                  | None -> failtest "the threaded zombie leader vanished before owner death"

                  Native.signalProcess anchor Native.SIGKILL |> ignore
                  let anchorClock = Stopwatch.StartNew()
                  let mutable anchorStat = readStat anchor

                  while
                      (anchorStat
                       |> Option.exists (fun stat ->
                           (stat.State = 'Z' || stat.State = 'X' || stat.State = 'x')
                           && stat.ThreadCount <= 1)
                       |> not)
                      && anchorClock.ElapsedMilliseconds < 3_000L
                      do
                      Threading.Thread.Sleep 10
                      anchorStat <- readStat anchor

                  Expect.isTrue
                      (anchorStat
                       |> Option.exists (fun stat ->
                           (stat.State = 'Z' || stat.State = 'X' || stat.State = 'x')
                           && stat.ThreadCount <= 1))
                      "the inert anchor cannot authorize cleanup; the threaded leader must do so"

                  host.Kill()
                  host.WaitForExit 3_000 |> ignore
                  Expect.isTrue
                      (waitForReap leader)
                      "the watchdog read field 20 as ${18} and killed the executable sibling thread"
                  Threading.Thread.Sleep 2_100
                  Expect.isFalse (File.Exists effectFile) "the live sibling never reached its delayed effect"
              finally
                  if not host.HasExited then host.Kill(true)
                  if group > 1 then Native.signalGroup group Native.SIGKILL |> ignore
                  if anchor > 1 then Native.signalProcess anchor Native.SIGKILL |> ignore
                  Directory.Delete(root, true)
          }

          test "owner death during TERM grace retains anchor provenance through KILL" {
              let root = tempRoot ()
              let registry = Path.Combine(root, "registry")
              let pidFile = Path.Combine(root, "effect.pid")
              let termFile = Path.Combine(root, "term.seen")
              Directory.CreateDirectory registry |> ignore

              let start = ProcessStartInfo(Environment.ProcessPath)

              if Path.GetFileNameWithoutExtension(Environment.ProcessPath) = "dotnet" then
                  start.ArgumentList.Add(Reflection.Assembly.GetExecutingAssembly().Location)

              start.ArgumentList.Add "--containment-term-grace-child"
              start.ArgumentList.Add registry
              start.ArgumentList.Add pidFile
              start.ArgumentList.Add termFile
              start.UseShellExecute <- false
              start.RedirectStandardOutput <- true
              start.RedirectStandardError <- true
              use host = Process.Start start

              let clock = Stopwatch.StartNew()

              while ((not (File.Exists termFile)
                      || (Directory.EnumerateFiles(registry, "*.group") |> Seq.isEmpty))
                     && clock.ElapsedMilliseconds < 5_000L) do
                  Threading.Thread.Sleep 10

              let effectPid =
                  match waitForPidFile pidFile with
                  | Some pid -> pid
                  | None ->
                      host.Kill(true)
                      failtest "TERM-resistant effect never established its pid precondition"

              let record = Directory.EnumerateFiles(registry, "*.group") |> Seq.exactlyOne
              let recordFields =
                  File.ReadAllText(record).Split(' ', StringSplitOptions.RemoveEmptyEntries)
              let anchorPid = Int32.Parse recordFields.[2]

              Expect.isTrue (File.Exists termFile) "the timeout entered TERM grace before owner death"
              Expect.isTrue (pidAlive effectPid) "the effect deliberately survived TERM"
              Expect.isTrue (pidAlive anchorPid) "the stopped anchor survived TERM as continuous provenance"

              host.Kill()
              host.WaitForExit 3_000 |> ignore
              Expect.isTrue (waitForReap effectPid) "EOF escalation killed the TERM-resistant effect"
              Expect.isTrue (waitForReap anchorPid) "EOF escalation killed the provenance anchor last"
              Directory.Delete(root, true)
          }

          test "owner death after the inner leader exits still reaps its background group" {
              let root = tempRoot ()
              let registry = Path.Combine(root, "registry")
              let pidFile = Path.Combine(root, "effect.pid")
              let readyFile = Path.Combine(root, "effect.ready")
              Directory.CreateDirectory registry |> ignore

              let start = ProcessStartInfo(Environment.ProcessPath)

              if Path.GetFileNameWithoutExtension(Environment.ProcessPath) = "dotnet" then
                  start.ArgumentList.Add(Reflection.Assembly.GetExecutingAssembly().Location)

              start.ArgumentList.Add "--containment-exited-leader-child"
              start.ArgumentList.Add registry
              start.ArgumentList.Add pidFile
              start.ArgumentList.Add readyFile
              start.UseShellExecute <- false
              start.RedirectStandardOutput <- true
              start.RedirectStandardError <- true
              use host = Process.Start start

              let clock = Stopwatch.StartNew()

              while ((not (File.Exists readyFile)
                      || (Directory.EnumerateFiles(registry, "*.group") |> Seq.isEmpty))
                     && clock.ElapsedMilliseconds < 5_000L) do
                  Threading.Thread.Sleep 10

              let effectPid =
                  match waitForPidFile pidFile with
                  | Some pid -> pid
                  | None ->
                      host.Kill(true)
                      failtest "background effect never established its non-vacuous pid precondition"

              let record = Directory.EnumerateFiles(registry, "*.group") |> Seq.exactlyOne
              let recordFields =
                  File.ReadAllText(record).Split(' ', StringSplitOptions.RemoveEmptyEntries)
              let anchorPid = Int32.Parse recordFields.[2]

              Expect.isTrue (pidAlive effectPid) "the descendant remained alive after its group leader exited"
              Expect.isTrue (pidAlive anchorPid) "the anchor prevents PGID reuse after leader exit"
              Expect.isFalse host.HasExited "the owner was still inside bounded post-leader drain/reap"
              Expect.isFalse
                  (Directory.EnumerateFiles(registry, "*.group") |> Seq.isEmpty)
                  "the exited leader's identity-bound group remained registered"

              host.Kill()
              host.WaitForExit 3_000 |> ignore
              Expect.isTrue
                  (waitForReap effectPid)
                  "the still-armed EOF watchdog reaped descendants across the leader-exit boundary"
              Expect.isTrue (waitForReap anchorPid) "the anchor was reaped with the original group"
              Directory.Delete(root, true)
          }

          test "EOF watchdog revalidates provenance between TERM and KILL" {
              let root = tempRoot ()
              let registry = Path.Combine(root, "registry")
              let pidFile = Path.Combine(root, "effect.pid")
              let readyFile = Path.Combine(root, "effect.ready")
              let termFile = Path.Combine(root, "watchdog.term")
              let killRelease = Path.Combine(root, "watchdog.kill.release")
              Directory.CreateDirectory registry |> ignore

              let start = ProcessStartInfo(Environment.ProcessPath)

              if Path.GetFileNameWithoutExtension(Environment.ProcessPath) = "dotnet" then
                  start.ArgumentList.Add(Reflection.Assembly.GetExecutingAssembly().Location)

              start.ArgumentList.Add "--containment-watchdog-revalidation-child"
              start.ArgumentList.Add registry
              start.ArgumentList.Add pidFile
              start.ArgumentList.Add readyFile
              start.ArgumentList.Add termFile
              start.ArgumentList.Add killRelease
              start.UseShellExecute <- false
              start.RedirectStandardOutput <- true
              start.RedirectStandardError <- true
              use host = Process.Start start
              let mutable effectPid = 0
              let mutable anchorPid = 0

              try
                  let clock = Stopwatch.StartNew()

                  while ((not (File.Exists readyFile)
                          || (Directory.EnumerateFiles(registry, "*.group") |> Seq.isEmpty))
                         && clock.ElapsedMilliseconds < 5_000L) do
                      Threading.Thread.Sleep 10

                  effectPid <-
                      match waitForPidFile pidFile with
                      | Some pid -> pid
                      | None -> failtest "TERM-resistant effect never established its pid"

                  let record = Directory.EnumerateFiles(registry, "*.group") |> Seq.exactlyOne
                  let fields = File.ReadAllText(record).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  anchorPid <- Int32.Parse fields.[2]

                  Expect.isTrue (pidAlive effectPid) "the background effect survived its exited leader"
                  Expect.isTrue (pidAlive anchorPid) "the stopped anchor held continuous group provenance"
                  host.Kill()
                  host.WaitForExit 3_000 |> ignore
                  Expect.isTrue (waitForFile termFile 3_000) "the watchdog delivered its authorized TERM"
                  Expect.isTrue (pidAlive effectPid) "the effect deliberately ignored TERM"

                  Native.signalProcess anchorPid Native.SIGKILL |> ignore
                  Expect.isTrue (waitForReap anchorPid) "the test removed provenance before escalation"
                  File.WriteAllText(killRelease, "release")
                  Threading.Thread.Sleep 500
                  Expect.isTrue
                      (pidAlive effectPid)
                      "fresh identity loss withheld the watchdog's otherwise lethal group KILL"
              finally
                  if not host.HasExited then host.Kill(true)
                  if effectPid > 1 then Native.signalProcess effectPid Native.SIGKILL |> ignore
                  if anchorPid > 1 then Native.signalProcess anchorPid Native.SIGKILL |> ignore
                  if effectPid > 1 then waitForReap effectPid |> ignore
                  if anchorPid > 1 then waitForReap anchorPid |> ignore
                  Directory.Delete(root, true)
          }

          test "the step leads its own process group" {
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

          test "FG-165: success waits for a delayed daemon to establish its precondition" {
              // A loaded runner can schedule the session leader through exit
              // before its background child runs. Without daemonScriptAfter's
              // bounded pid-file handshake, successful group reaping kills the
              // delayed child before it records its pid and this test fails.
              let root = Path.Combine(tempRoot (), "fixture ' $ [x]")
              Directory.CreateDirectory root |> ignore
              let req = request root ""
              let pidFile = Path.Combine(req.Workspace, "delayed-daemon.pid")

              let r =
                  Executor.runStep
                      { req with
                          Script = Some(daemonScriptAfter 250 pidFile + " echo spawned") }

              Expect.equal r.Status Success "the synchronized step succeeds"

              match waitForPidFile pidFile with
              | None -> failtest "the delayed daemon was reaped before establishing the test precondition"
              | Some pid ->
                  Expect.isTrue (waitForReap pid) $"delayed daemon {pid} must be reaped after success"

                  match r.Termination with
                  | Some t -> Expect.equal t.LeakedProcesses 0 "the established daemon was fully reaped"
                  | None -> failtest "successful group reaping must report its termination"
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

let eventDrivenWaits =
    testList
        "FG-197 event-driven process completion"
        [ test "the reported group id is the inner session leader, not the setsid wrapper" {
              let result =
                  ProcessGroup.run
                      { RunRequest.create ("printf 'leader=%s\\n' \"$$\"", tempRoot ()) with
                          ReapGroup = false }

              let leader =
                  result.Stdout.Replace("\r\n", "\n").Split '\n'
                  |> Array.tryPick (fun line ->
                      if line.StartsWith "leader=" then
                          match Int32.TryParse(line.Substring("leader=".Length)) with
                          | true, pid -> Some pid
                          | _ -> None
                      else
                          None)

              Expect.isSome leader "the script exposed its actual session-leader pid"
              Expect.equal result.ProcessGroupId leader "the marker, not Process.Id, owns group identity"
          }

          test "continuous output consumes only the shared settlement budget" {
              // Model 17ms already spent draining an active callback, then make
              // every before/after snapshot differ. This is a deterministic
              // budget proof: no scheduler or wall-clock tolerance participates.
              let mutable elapsed = 17L
              let sleeps = ResizeArray<int>()
              let mutable sample = 0

              let snapshot () =
                  sample <- sample + 1
                  sample, -sample

              ProcessGroup.settleOutputUntilQuiet
                  300
                  (fun () -> elapsed)
                  (fun milliseconds ->
                      sleeps.Add milliseconds
                      elapsed <- elapsed + int64 milliseconds)
                  snapshot

              Expect.sequenceEqual
                  sleeps
                  [ 40; 40; 40; 40; 40; 40; 40; 3 ]
                  "the final sample sleeps only the three milliseconds left in the shared budget"
              Expect.equal elapsed 300L "continuous output cannot overshoot the 300ms settlement budget"
          }

          test "a zero settlement budget neither samples nor sleeps" {
              let mutable samples = 0
              let mutable sleeps = 0

              ProcessGroup.settleOutputUntilQuiet
                  0
                  (fun () -> 0L)
                  (fun _ -> sleeps <- sleeps + 1)
                  (fun () ->
                      samples <- samples + 1
                      samples, samples)

              Expect.equal samples 0 "zero budget never opens a quiet-sampling window"
              Expect.equal sleeps 0 "zero budget never sleeps"
          }

          test "one quiet sample followed by output restarts the sustained quiet window" {
              let mutable elapsed = 0L
              let sleeps = ResizeArray<int>()
              let samples =
                  Collections.Generic.Queue<int * int>(
                      [ (0, 0); (0, 0); (1, 0); (1, 0); (1, 0) ])

              ProcessGroup.settleOutputUntilQuiet
                  300
                  (fun () -> elapsed)
                  (fun milliseconds ->
                      sleeps.Add milliseconds
                      elapsed <- elapsed + int64 milliseconds)
                  (fun () -> samples.Dequeue())

              Expect.sequenceEqual
                  sleeps
                  [ 40; 40; 40; 40 ]
                  "a change after one quiet interval resets the consecutive-quiet counter"
              Expect.equal elapsed 160L "two fresh unchanged intervals close the window after the reset"
              Expect.isEmpty samples "the retained sample sequence is consumed exactly"
          }

          test "a delayed tail after the direct process exits is retained" {
              let result =
                  ProcessGroup.run
                      { RunRequest.create (
                            "#!/bin/sh\nsetsid /bin/sh -c 'sleep 0.3; printf \"delayed-tail\\n\"' & printf 'leader-done\\n'",
                            tempRoot ()) with
                          ReapGroup = false }

              Expect.equal result.Outcome (Completed 0) "the direct step completed"
              Expect.equal
                  (result.Stdout.Replace("\r\n", "\n"))
                  "leader-done\ndelayed-tail\n"
                  "a no-xtrace script preserves the actual delayed tail in exact emission order"
          }

          test "an escaped descendant holding stdout cannot wedge or erase arrived capture" {
              let root = tempRoot ()
              let pidFile = Path.Combine(root, "escaped.pid")
              let sw = Diagnostics.Stopwatch.StartNew()
              let streamed = Collections.Concurrent.ConcurrentQueue<string>()

              let result =
                  ProcessGroup.run
                      { RunRequest.create (
                            $"setsid /bin/sh -c 'echo $$ > {pidFile}; sleep 30' 2>/dev/null & printf arrived",
                            root) with
                          SuppressStdoutEcho = true
                          ReapGroup = false
                          OnLine = Some streamed.Enqueue }

              sw.Stop()
              match waitForPidFile pidFile with
              | Some pid ->
                  Native.signalGroup pid Native.SIGKILL |> ignore
                  Expect.isTrue (waitForReap pid) "the escaped fixture is cleaned up"
              | None -> failtest "the escaped descendant never established its pipe-holding precondition"

              Expect.equal result.Outcome (Completed 0) "the direct step completed"
              Expect.isLessThan sw.ElapsedMilliseconds 2_000L "capture reuses the reader bound instead of adding a second five-second wait"
              Expect.equal result.Stdout "arrived" "bytes received before the bound survive truncation"
          }

          test "a production containment anchor is reaped before callback EOF is required" {
              let root = tempRoot ()
              let registry = Path.Combine(root, "registry")
              let durableRoot = root + "@tmp"
              let previousRegistry = Environment.GetEnvironmentVariable "FOGELL_PROCESS_GROUP_REGISTRY"
              let streamed = Collections.Concurrent.ConcurrentQueue<string>()
              Directory.CreateDirectory registry |> ignore
              Environment.SetEnvironmentVariable("FOGELL_PROCESS_GROUP_REGISTRY", registry)

              try
                  let result =
                      ProcessGroup.run
                          { RunRequest.create ("printf 'controller-line\\n'", root) with
                              OnLine = Some streamed.Enqueue }

                  Expect.equal result.Outcome (Completed 0) "the controller-style step completes"
                  Expect.isTrue
                      (streamed.ToArray() |> Array.contains "controller-line")
                      "the output callback is published before the run returns"
                  Expect.isEmpty
                      (Directory.EnumerateFiles registry)
                      "normal reaping removes the controller containment record"
              finally
                  Environment.SetEnvironmentVariable("FOGELL_PROCESS_GROUP_REGISTRY", previousRegistry)
                  if Directory.Exists root then Directory.Delete(root, true)
                  if Directory.Exists durableRoot then Directory.Delete(durableRoot, true)
          }

          test "an open callback reader fails closed and cannot enqueue a late line" {
              let root = tempRoot ()
              let pidFile = Path.Combine(root, "escaped-callback.pid")
              let streamed = Collections.Concurrent.ConcurrentQueue<string>()
              let sw = Diagnostics.Stopwatch.StartNew()

              try
                  Expect.throwsT<TimeoutException>
                      (fun () ->
                          ProcessGroup.run
                              { RunRequest.create (
                                    $"setsid /bin/sh -c 'echo $$ > {pidFile}; sleep 0.8; printf \"late-line\\n\" >&2; sleep 30' & i=0; while [ ! -s {pidFile} ]; do i=$((i+1)); [ \"$i\" -lt 300 ] || exit 97; sleep 0.01; done; printf \"early-line\\n\"",
                                    root) with
                                  SuppressStdoutEcho = true
                                  ReapGroup = false
                                  OnLine = Some streamed.Enqueue }
                          |> ignore)
                      "a callback-producing reader without EOF cannot return success"

                  sw.Stop()
                  Expect.isLessThan
                      sw.ElapsedMilliseconds
                      1_500L
                      "reader noncompletion consumes only the existing output-drain bound"

                  let countAtFailure = streamed.Count
                  Threading.Thread.Sleep 600
                  Expect.equal
                      streamed.Count
                      countAtFailure
                      "callback admission closes before the tail snapshot"
                  Expect.isFalse
                      (streamed.ToArray() |> Array.contains "late-line")
                      "the escaped writer cannot enqueue after the failed run"
              finally
                  match waitForPidFile pidFile with
                  | Some pid ->
                      Native.signalGroup pid Native.SIGKILL |> ignore
                      Expect.isTrue (waitForReap pid) "the escaped callback fixture is cleaned up"
                  | None -> failtest "the escaped callback writer never established its precondition"
          }

          test "capture timeout pays one post-signal reader bound and keeps partial output" {
              let root = tempRoot ()
              let pidFile = Path.Combine(root, "escaped-timeout.pid")
              let sw = Diagnostics.Stopwatch.StartNew()

              let result =
                  ProcessGroup.run
                      { RunRequest.create (
                            $"setsid /bin/sh -c 'echo $$ > {pidFile}; sleep 30' & i=0; while [ ! -s {pidFile} ]; do i=$((i+1)); [ \"$i\" -lt 300 ] || exit 97; sleep 0.01; done; printf arrived; sleep 30",
                            root) with
                          SuppressStdoutEcho = true
                          TimeoutMs = Some 500L
                          GraceMs = 100
                          ReapGroup = false }

              sw.Stop()
              match waitForPidFile pidFile with
              | Some pid ->
                  Native.signalGroup pid Native.SIGKILL |> ignore
                  Expect.isTrue (waitForReap pid) "the escaped timeout fixture is cleaned up"
              | None -> failtest "the escaped timeout descendant never established its precondition"

              Expect.equal result.Outcome TimedOut "the foreground step reached its timeout"
              Expect.isLessThan sw.ElapsedMilliseconds 2_000L "capture has no second post-result or duplicate reader wait"
              Expect.equal result.Stdout "arrived" "timeout keeps bytes captured before the bounded snapshot"
          }

          test "a delayed pre-signal user Terminated line does not suppress synthetic narration" {
              let root = tempRoot ()
              let readyFile = Path.Combine(root, "user-terminated.ready")
              let signalFile = Path.Combine(root, "user-terminated.signal")
              use callbackEntered = new Threading.ManualResetEventSlim(false)
              use interruptObserved = new Threading.ManualResetEventSlim(false)
              use abortFixture = new Threading.ManualResetEventSlim(false)
              use releaseCallback = new Threading.ManualResetEventSlim(false)
              let streamed = Collections.Concurrent.ConcurrentQueue<string>()

              let releaser =
                  Threading.Tasks.Task.Run(fun () ->
                      let interrupted = interruptObserved.Wait 3_000
                      if not interrupted then abortFixture.Set()
                      // The trap runs only after ProcessGroup has taken its
                      // pre-signal output snapshot and delivered TERM. Waiting
                      // for its marker keeps the already-ingested user callback
                      // in flight across that boundary without a timing guess.
                      let signalled = interrupted && waitForFile signalFile 3_000
                      releaseCallback.Set()
                      interrupted, signalled)

              let onLine line =
                  streamed.Enqueue line

                  if line = "Terminated" then
                      callbackEntered.Set()
                      if not (releaseCallback.Wait 3_000) then
                          failwith "TERM did not cross the pre-signal snapshot while the user callback was held"

              let result =
                  ProcessGroup.run
                      { RunRequest.create (
                            $"#!/bin/sh\ntrap 'printf signal > \"{signalFile}\"; exit 0' TERM\necho Terminated\nprintf ready > '{readyFile}'\nwhile :; do :; done",
                            root) with
                          GraceMs = 500
                          Interrupt =
                              Some(fun () ->
                                  // The callback starts only after the reader has
                                  // appended the line to its classification sink.
                                  // Requiring both handshakes makes the line
                                  // deterministically pre-signal while its user
                                  // callback remains deliberately incomplete.
                                  let ready = File.Exists readyFile && callbackEntered.IsSet
                                  if ready then interruptObserved.Set()
                                  // A reader/callback regression must fail this
                                  // test promptly instead of leaving its endless
                                  // shell alive until the outer CI timeout.
                                  ready || abortFixture.IsSet)
                          OnLine = Some onLine }

              let interruptCrossed, signalCrossed = releaser.GetAwaiter().GetResult()
              Expect.isTrue (File.Exists readyFile) "the script printed before requesting interruption"
              Expect.isTrue callbackEntered.IsSet "the pre-signal user callback was genuinely delayed"
              Expect.isTrue interruptCrossed "the fixture observed the interrupt before releasing the callback"
              Expect.isTrue signalCrossed "the callback remained delayed until TERM crossed the snapshot boundary"
              Expect.equal result.Outcome Cancelled "the synchronized interrupt ended the step"

              let terminatedCount =
                  streamed.ToArray()
                  |> Array.filter (fun line -> line.Trim() = "Terminated")
                  |> Array.length

              Expect.equal terminatedCount 2 "pre-signal user output remains distinct from synthetic post-signal narration"
          }

          test "a Terminated callback delayed across the signal boundary is emitted exactly once" {
              use callbackEntered = new Threading.ManualResetEventSlim(false)
              use releaseCallback = new Threading.ManualResetEventSlim(false)
              let streamed = Collections.Concurrent.ConcurrentQueue<string>()

              let releaser =
                  Threading.Tasks.Task.Run(fun () ->
                      if callbackEntered.Wait 3_000 then
                          // This callback is emitted by the TERM trap, so it starts
                          // strictly after the signal. Keep the following Terminated
                          // callback queued long enough to make post-signal draining
                          // load-bearing, but within its 300ms upper bound.
                          Threading.Thread.Sleep 150

                      releaseCallback.Set())

              let onLine line =
                  streamed.Enqueue line

                  if line = "hold-post-callback" then
                      callbackEntered.Set()
                      releaseCallback.Wait 3_000 |> ignore

              let result =
                  ProcessGroup.run
                      { RunRequest.create (
                            "#!/bin/sh\ntrap 'echo hold-post-callback; echo \"Terminated \"; exit 0' TERM\nwhile :; do :; done",
                            tempRoot ()) with
                          TimeoutMs = Some 500L
                          GraceMs = 500
                          OnLine = Some onLine }

              releaser.GetAwaiter().GetResult()
              Expect.isTrue callbackEntered.IsSet "the hostile callback was blocked before timeout"
              Expect.equal result.Outcome TimedOut "the timeout signal reached the trapping shell"

              let terminatedCount =
                  streamed.ToArray()
                  |> Array.filter (fun line -> line.Trim() = "Terminated")
                  |> Array.length

              Expect.contains (streamed.ToArray()) "Terminated " "the delayed shell callback itself drained before return"
              let transcript = String.concat " | " (streamed.ToArray())

              Expect.equal
                  terminatedCount
                  1
                  $"the delayed shell line suppresses synthetic duplicate narration: {transcript}"
          }

          test "an OnLine callback failure is sticky and escapes the run" {
              let seen = ResizeArray<string>()

              Expect.throwsT<IO.IOException>
                  (fun () ->
                      ProcessGroup.run
                          { RunRequest.create ("printf 'first\\nsecond\\n'", tempRoot ()) with
                              ReapGroup = false
                              OnLine =
                                  Some(fun line ->
                                      seen.Add line
                                      raise (IO.IOException "event sink unavailable")) }
                      |> ignore)
                  "a completed faulted callback tail must fail the run"

              Expect.equal seen.Count 1 "successors do not publish after the first callback failure"
          }

          test "an incomplete OnLine callback tail fails within the shared bound" {
              use callbackEntered = new Threading.ManualResetEventSlim(false)
              use releaseCallback = new Threading.ManualResetEventSlim(false)
              use callbackCompleted = new Threading.ManualResetEventSlim(false)
              let sw = Diagnostics.Stopwatch.StartNew()

              try
                  Expect.throwsT<TimeoutException>
                      (fun () ->
                          ProcessGroup.run
                              { RunRequest.create ("printf 'blocked\\n'", tempRoot ()) with
                                  ReapGroup = false
                                  OnLine =
                                      Some(fun _ ->
                                          callbackEntered.Set()

                                          try
                                              releaseCallback.Wait()
                                          finally
                                              callbackCompleted.Set()) }
                          |> ignore)
                      "an incomplete callback tail cannot return a successful RunResult"

                  sw.Stop()
                  Expect.isTrue callbackEntered.IsSet "the callback genuinely held the tail open"
                  Expect.isLessThan
                      sw.ElapsedMilliseconds
                      1_500L
                      "callback noncompletion preserves the existing bounded output-drain elapsed time"
              finally
                  releaseCallback.Set()
                  Expect.isTrue
                      (callbackCompleted.Wait 1_000)
                      "the fixture releases the rejected callback task before the test exits"
          }

          test "fast completed steps do not pay a fixed reader-settlement window" {
              let sw = Diagnostics.Stopwatch.StartNew()

              for _ in 1 .. 10 do
                  let result =
                      ProcessGroup.run
                          { RunRequest.create ("true", tempRoot ()) with
                              ReapGroup = false }

                  Expect.equal result.Outcome (Completed 0) "each fast control completed"

              sw.Stop()
              Expect.isLessThan sw.ElapsedMilliseconds 400L "ten steps have no tenfold 40ms floor"
          }

          test "an earlier interrupt remains cancellation when the local budget is also expired" {
              let result =
                  ProcessGroup.run
                      { RunRequest.create ("sleep 30", tempRoot ()) with
                          TimeoutMs = Some 0L
                          GraceMs = 100
                          Interrupt = Some(fun () -> true)
                          InterruptBeatsDeadline = Some(fun () -> true) }

              Expect.equal result.Outcome Cancelled "the caller's earlier interrupt timestamp is authoritative"
          }

          test "an earlier deadline remains timeout when an interrupt is also observable" {
              let result =
                  ProcessGroup.run
                      { RunRequest.create ("sleep 30", tempRoot ()) with
                          TimeoutMs = Some 0L
                          GraceMs = 100
                          Interrupt = Some(fun () -> true)
                          InterruptBeatsDeadline = Some(fun () -> false) }

              Expect.equal result.Outcome TimedOut "the caller's earlier deadline timestamp is authoritative"
          } ]

/// FG-044b(c). Raw nested-call keys are identifiers, not suffix searches.
let credentialKeyBoundaries =
    let prefixes =
        [ "ASCII letter", "X"
          "ASCII digit", "7"
          "Arabic-Indic decimal digit", "\u0667"
          "Roman letter number", "\u2167"
          "underscore connector", "_"
          "dollar currency", "$"
          "Unicode letter", "λ"
          "nonspacing mark", "\u0301"
          "spacing combining mark", "\u093E"
          "connector punctuation", "\u203F"
          "Unicode currency", "€"
          "zero-width non-joiner", "\u200C"
          "zero-width joiner", "\u200D" ]

    let hostile prefix =
        [ "string credentialsId", "string", $"string({prefix}credentialsId: 'text-id', variable: 'TOKEN')"
          "string variable", "string", $"string(credentialsId: 'text-id', {prefix}variable: 'TOKEN')"
          "file credentialsId", "file", $"file({prefix}credentialsId: 'file-id', variable: 'CERT')"
          "file variable", "file", $"file(credentialsId: 'file-id', {prefix}variable: 'CERT')"
          "userpass credentialsId",
          "usernamePassword",
          $"usernamePassword({prefix}credentialsId: 'user-id', usernameVariable: 'USER', passwordVariable: 'PASS')"
          "userpass usernameVariable",
          "usernamePassword",
          $"usernamePassword(credentialsId: 'user-id', {prefix}usernameVariable: 'USER', passwordVariable: 'PASS')"
          "userpass passwordVariable",
          "usernamePassword",
          $"usernamePassword(credentialsId: 'user-id', usernameVariable: 'USER', {prefix}passwordVariable: 'PASS')" ]

    testList
        "FG-044b(c) credential keys require a complete identifier token"
        [ test "every required key rejects every hostile identifier-part prefix" {
              for prefixLabel, prefix in prefixes do
                  for keyLabel, kind, source in hostile prefix do
                      for quoteLabel, quotedSource in
                          [ "single quoted", source
                            "double quoted", source.Replace("'", "\"") ] do
                          let requests = Credentials.parseRequests quotedSource

                          match requests with
                          | [ BindUnmodelled(actualKind, actualSource) ] ->
                              Expect.equal
                                  actualKind
                                  kind
                                  $"{prefixLabel} / {keyLabel} / {quoteLabel}: binding kind remains observable"

                              Expect.isNonEmpty
                                  actualSource
                                  $"{prefixLabel} / {keyLabel} / {quoteLabel}: rejected source remains observable"
                          | other ->
                              failtestf "%s / %s / %s parsed as supported: %A" prefixLabel keyLabel quoteLabel other

                          Expect.isEmpty
                              (Credentials.idsOf requests)
                              $"{prefixLabel} / {keyLabel} / {quoteLabel}: an unmodelled request invents no credential id"
          }

          test "exact case-sensitive keys preserve all three supported bindings" {
              let source =
                  "string ( credentialsId : 'text-id', variable : \"TOKEN\" ), "
                  + "file(credentialsId:\"file-id\", variable:'CERT'), "
                  + "usernamePassword(credentialsId: 'user-id', usernameVariable: \"USER\", passwordVariable: 'PASS')"

              let requests = Credentials.parseRequests source

              Expect.equal
                  requests
                  [ BindText("text-id", "TOKEN")
                    BindFile("file-id", "CERT")
                    BindUserPass("user-id", "USER", "PASS") ]
                  "single/double quotes and whitespace remain valid"

              Expect.equal
                  (Credentials.idsOf requests)
                  [ "text-id"; "file-id"; "user-id" ]
                  "idsOf returns exactly the modeled ids"
          }

          test "credential trivia uses Groovy's exact ASCII whitespace set" {
              let groovyWhitespace c =
                  c = ' ' || c = '\t' || c = '\r' || c = '\n' || c = '\u000C'

              for trivia in [ " "; "\t"; "\r"; "\n"; "\u000C"; " \t\r\n\u000C" ] do
                  let source =
                      $"{trivia}string{trivia}({trivia}credentialsId{trivia}:{trivia}'text-id'{trivia},{trivia}variable{trivia}:{trivia}'TOKEN'{trivia}){trivia}"

                  Expect.equal
                      (Credentials.parseRequests source)
                      [ BindText("text-id", "TOKEN") ]
                      "every Groovy WS code point remains valid between structural tokens"

              let unicodeSurplus =
                  [ for code in 0 .. 0xFFFF do
                        let c = char code

                        if Char.IsWhiteSpace c && not (groovyWhitespace c) then
                            yield c ]

              Expect.contains unicodeSurplus '\u00A0' "the exhaustive surplus includes NBSP"

              for invalid in unicodeSurplus do
                  let token = string invalid

                  for position, source in
                      [ "leading", token + "string(credentialsId: 'text-id', variable: 'TOKEN')"
                        "kind/call", "string" + token + "(credentialsId: 'text-id', variable: 'TOKEN')"
                        "argument/key", "string(" + token + "credentialsId: 'text-id', variable: 'TOKEN')"
                        "key/colon", "string(credentialsId" + token + ": 'text-id', variable: 'TOKEN')"
                        "colon/value", "string(credentialsId:" + token + "'text-id', variable: 'TOKEN')"
                        "value/comma", "string(credentialsId: 'text-id'" + token + ", variable: 'TOKEN')"
                        "call/trailing", "string(credentialsId: 'text-id', variable: 'TOKEN')" + token ] do
                      let requests = Credentials.parseRequests source

                      Expect.isTrue
                          (requests |> List.forall (function BindUnmodelled _ -> true | _ -> false))
                          $"U+{int invalid:X4}/{position}: non-Groovy trivia fails the complete request closed"

                      Expect.isEmpty
                          (Credentials.idsOf requests)
                          $"U+{int invalid:X4}/{position}: invalid trivia cannot expose credential authority"
          }

          test "key spelling remains case-sensitive" {
              for source in
                  [ "string(CredentialsId: 'text-id', variable: 'TOKEN')"
                    "file(credentialsId: 'file-id', Variable: 'CERT')"
                    "usernamePassword(credentialsId: 'user-id', UsernameVariable: 'USER', passwordVariable: 'PASS')" ] do
                  let requests = Credentials.parseRequests source

                  match requests with
                  | [ BindUnmodelled _ ] -> ()
                  | other -> failtestf "wrong-case key parsed as supported: %A" other

                  Expect.isEmpty (Credentials.idsOf requests) "wrong-case keys invent no ids"
          }

          test "non-identifier prefixes are not discarded to reveal a supported suffix" {
              for label, prefix in
                  [ "other number", "\u00B2"
                    "enclosing mark", "\u20DD" ] do
                  let single = $"string({prefix}credentialsId: 'text-id', variable: 'TOKEN')"

                  for quoteLabel, source in
                      [ "single quoted", single
                        "double quoted", single.Replace("'", "\"") ] do
                      let requests = Credentials.parseRequests source

                      Expect.isTrue
                          (requests |> List.forall (function BindUnmodelled _ -> true | _ -> false))
                          $"{label}/{quoteLabel}: invalid leading syntax fails the complete call closed"

                      Expect.isEmpty
                          (Credentials.idsOf requests)
                          $"{label}/{quoteLabel}: no supported suffix becomes a credential id"
          }

          test "quoted commented and whole-item decoys never materialize requests" {
              let decoys =
                  [ "string(note: \"credentialsId: 'decoy'\", variable: 'TOKEN')"
                    "string(note: 'credentialsId: \"decoy\"', variable: 'TOKEN')"
                    "string(/* credentialsId: 'decoy', */ variable: 'TOKEN')"
                    "string(// credentialsId: 'decoy'\n variable: 'TOKEN')"
                    "[\"string(credentialsId: 'decoy', variable: 'TOKEN')\"]"
                    "[/* string(credentialsId: 'decoy', variable: 'TOKEN') */ bogus()]" ]

              for source in decoys do
                  let requests = Credentials.parseRequests source
                  Expect.isNonEmpty requests $"decoy source is an explicit refusal: {source}"
                  Expect.isTrue
                      (requests |> List.forall (function BindUnmodelled _ -> true | _ -> false))
                      $"decoy source cannot materialize a supported binding: {source}"
          }

          test "malformed segments and trailing source fail the complete request closed" {
              let valid = "string(credentialsId: 'text-id', variable: 'TOKEN')"

              for source in
                  [ valid + ", ignored"
                    valid + ","
                    valid + " trailing"
                    "[" + valid + ",,file(credentialsId: 'file-id', variable: 'CERT')]"
                    "string(credentialsId: 'text-id', variable: 'TOKEN', junk: helper('x'))"
                    "string(credentialsId: 'text-id', variable: 'TOKEN', junk: [1, 2])"
                    "string(credentialsId: 'text-id', variable: 'TOKEN', junk: { -> 1 })"
                    "string(credentialsId: 'text-id', variable: 'TOKEN', junk: 'literal')" ] do
                  let requests = Credentials.parseRequests source
                  Expect.isNonEmpty requests $"malformed source is an explicit refusal: {source}"
                  Expect.isTrue
                      (requests |> List.exists (function BindUnmodelled _ -> true | _ -> false))
                      $"the complete request retains a refusal beside any valid sibling: {source}"
          }

          test "unknown literal keys retain Jenkins warning metadata" {
              let requests =
                  Credentials.parseRequests
                      "string(credentialsId: 'text-id', notvariable: 'TOKEN', another: 'x')"

              match requests with
              | [ request ] ->
                  Expect.equal
                      (Credentials.unknownParameterWarning request)
                      (Some(
                          "org.jenkinsci.plugins.credentialsbinding.impl.StringBinding",
                          [ "notvariable"; "another" ]))
                      "class and unknown keys retain source order"
              | other -> failtestf "unknown-key call was not one explicit refusal: %A" other
          }

          test "duplicate keys and unsupported quoted forms fail closed" {
              for source in
                  [ "string(credentialsId: 'first', credentialsId: 'second', variable: 'TOKEN')"
                    "string(credentialsId: 'text-id', variable: 'FIRST', variable: 'SECOND')"
                    "string(credentialsId: \"$ID\", variable: 'TOKEN')"
                    "string(credentialsId: \"${ID}\", variable: 'TOKEN')"
                    "string(credentialsId: 'text-id', variable: \"TOKEN_${SUFFIX}\")"
                    "string(credentialsId: \"\\u0024{ID}\", variable: 'TOKEN')"
                    "string(credentialsId: 'fogell\\u002dtoken', variable: 'TOKEN')"
                    "string(credentialsId: \"fogell\\u002dtoken\", variable: 'TOKEN')"
                    "string(credentialsId: '''text-id''', variable: 'TOKEN')"
                    "string(credentialsId: \"\"\"text,id)\"\"\", variable: 'TOKEN')"
                    "string(note: '''file(credentialsId: 'decoy', variable: 'CERT'),''', credentialsId: 'text-id', variable: 'TOKEN')"
                    "string(credentialsId: 'live\ntext,)', variable: 'TOKEN')"
                    "string(credentialsId: \"live\rtext,)\", variable: 'TOKEN')"
                    "string(credentialsId: 'text-id', variable: 'TOKEN\r\nNEXT')" ] do
                  let requests = Credentials.parseRequests source
                  Expect.isNonEmpty requests $"unsupported source is an explicit refusal: {source}"
                  Expect.isTrue
                      (requests |> List.forall (function BindUnmodelled _ -> true | _ -> false))
                      $"unsupported source cannot materialize a credential: {source}"
          } ]

/// FG-044b(d). Jenkins stash uses Ant's default excludes unless the exact
/// useDefaultExcludes flag is false. Keep this at the Stash boundary so every
/// caller shares the same selection rule and a walker-only fix cannot pass.
let stashDefaultExcludes =
    testList
        "FG-044b(d) stash Ant default excludes"
        [ test "all 28 defaults are case-sensitive, literal includes cannot override them, and false opts out" {
              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let store = StashStore.under (Path.Combine(root, "controller"))
              Directory.CreateDirectory workspace |> ignore

              let excluded =
                  [ "temp/result.txt~"
                    "temp/#result.txt#"
                    "temp/.#result.txt"
                    "temp/%result.txt%"
                    "temp/._result.txt"
                    "files/CVS"
                    "directories/CVS/hidden.txt"
                    "meta/.cvsignore"
                    "files/SCCS"
                    "directories/SCCS/hidden.txt"
                    "meta/vssver.scc"
                    "files/.svn"
                    "directories/.svn/hidden.txt"
                    "files/.git"
                    "directories/.git/hidden.txt"
                    "meta/.gitattributes"
                    "meta/.gitignore"
                    "meta/.gitmodules"
                    "files/.hg"
                    "directories/.hg/hidden.txt"
                    "meta/.hgignore"
                    "meta/.hgsub"
                    "meta/.hgsubstate"
                    "meta/.hgtags"
                    "files/.bzr"
                    "directories/.bzr/hidden.txt"
                    "meta/.bzrignore"
                    "meta/.DS_Store" ]

              let caseNear =
                  [ "temp/result.txt~x"
                    "temp/x#result.txt#"
                    "temp/x.#result.txt"
                    "temp/x%result.txt%"
                    "temp/x._result.txt"
                    "files/Cvs"
                    "directories/Cvs/hidden.txt"
                    "meta/.CVSIGNORE"
                    "files/Sccs"
                    "directories/Sccs/hidden.txt"
                    "meta/VSSVER.SCC"
                    "files/.SVN"
                    "directories/.SVN/hidden.txt"
                    "files/.GIT"
                    "directories/.GIT/hidden.txt"
                    "meta/.GitAttributes"
                    "meta/.GitIgnore"
                    "meta/.GitModules"
                    "files/.HG"
                    "directories/.HG/hidden.txt"
                    "meta/.HGIgnore"
                    "meta/.HGSub"
                    "meta/.HGSubState"
                    "meta/.HGTags"
                    "files/.BZR"
                    "directories/.BZR/hidden.txt"
                    "meta/.BZRIgnore"
                    "meta/.DS_STORE" ]

              let write relative =
                  let path = Path.Combine(workspace, relative)
                  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                  File.WriteAllText(path, relative)

              let literal = "literal/.git/config"

              for relative in excluded @ caseNear @ [ literal; "visible.txt"; "user/drop.txt" ] do
                  write relative

              let save name patterns defaults =
                  match
                      Stash.save
                          store
                          "build-1"
                          workspace
                          name
                          patterns
                          [ "user/drop.txt" ]
                          defaults
                          (fun () -> false)
                  with
                  | Ok(saved, _) -> saved
                  | Error problem -> failtest problem.Describe

              let savedDefault = save "defaults" [ "**" ] true
              let expectedDefault = caseNear @ [ "visible.txt" ] |> Set.ofList

              Expect.equal
                  (Set.ofList savedDefault)
                  expectedDefault
                  "all defaults are removed, every case-near path remains, and caller excludes still win"

              let savedWithoutDefaults = save "disabled" [ "**" ] false
              let expectedWithoutDefaults = excluded @ caseNear @ [ literal; "visible.txt" ] |> Set.ofList

              Expect.equal
                  (Set.ofList savedWithoutDefaults)
                  expectedWithoutDefaults
                  "false disables only Ant defaults and never disables the caller's excludes"

              Expect.isEmpty (save "literal-default" [ literal ] true) "a literal include cannot override a default exclude"
              Expect.equal (save "literal-disabled" [ literal ] false) [ literal ] "false admits that same literal path"

              Directory.Delete(workspace, true)
              Directory.CreateDirectory workspace |> ignore

              match Stash.restore store "build-1" workspace "defaults" (fun () -> false) with
              | Error why -> failtestf "default-filtered stash did not restore: %s" why
              | Ok restored ->
                  Expect.equal restored savedDefault "restore returns the exact filtered stash inventory"
                  Expect.isTrue (File.Exists(Path.Combine(workspace, "visible.txt"))) "ordinary content restores"
                  Expect.isTrue
                      (File.Exists(Path.Combine(workspace, caseNear.Head)))
                      "a case-near control restores"
                  Expect.isFalse
                      (File.Exists(Path.Combine(workspace, excluded.Head)))
                      "a default-excluded file was never copied into controller storage"
          } ]

/// FG-228. Fogell deliberately takes a stricter policy than the pinned Jenkins
/// link behavior: every selected symbolic-link path refuses before controller
/// bytes are replaced. These tests hold save, descriptor and restore boundaries
/// beneath the public walker.
let stashSymlinkContainment =
    let write (path: string) (value: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, value)

    let arrange root shape =
        let workspace = Path.Combine(root, "workspace")
        let outside = Path.Combine(root, "outside")
        Directory.CreateDirectory workspace |> ignore
        write (Path.Combine(workspace, "ordinary.txt")) "ordinary"

        let pattern, link =
            match shape with
            | "in-file" ->
                write (Path.Combine(workspace, "hidden", "in-file.txt")) "inside-file-sentinel"
                let link = Path.Combine(workspace, "in-file-link")
                File.CreateSymbolicLink(link, Path.Combine("hidden", "in-file.txt")) |> ignore
                "in-file-link", "in-file-link"
            | "in-dir" ->
                write (Path.Combine(workspace, "hidden", "in-dir", "value.txt")) "inside-dir-sentinel"
                let link = Path.Combine(workspace, "in-dir-link")
                Directory.CreateSymbolicLink(link, Path.Combine("hidden", "in-dir")) |> ignore
                "in-dir-link/**", "in-dir-link"
            | "out-file" ->
                write (Path.Combine(outside, "out-file.txt")) "outside-file-sentinel"
                let link = Path.Combine(workspace, "out-file-link")
                File.CreateSymbolicLink(link, Path.Combine(outside, "out-file.txt")) |> ignore
                "out-file-link", "out-file-link"
            | "out-dir" ->
                write (Path.Combine(outside, "out-dir", "value.txt")) "outside-dir-sentinel"
                let link = Path.Combine(workspace, "out-dir-link")
                Directory.CreateSymbolicLink(link, Path.Combine(outside, "out-dir")) |> ignore
                "out-dir-link/**", "out-dir-link"
            | other -> failwith $"unknown link shape {other}"

        workspace, pattern, link

    let inventory root =
        if Directory.Exists root then
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            |> Array.map (fun path -> Path.GetRelativePath(root, path), File.ReadAllText path)
            |> Array.sortBy fst
            |> Array.toList
        else
            []

    let tests =
        [ test "each selected link shape independently refuses under both default-exclude modes and preserves the prior stash" {
              for shape in [ "in-file"; "in-dir"; "out-file"; "out-dir" ] do
                for useDefaults in [ true; false ] do
                  let root = tempRoot ()
                  let workspace, pattern, refusedPath = arrange root shape
                  let store = StashStore.under (Path.Combine(root, "controller"))

                  match
                      Stash.save
                          store
                          "build-1"
                          workspace
                          "links"
                          [ "ordinary.txt" ]
                          []
                          useDefaults
                          (fun () -> false)
                  with
                  | Error problem -> failtest problem.Describe
                  | Ok _ -> ()

                  let before = inventory store.Root

                  match
                      Stash.save
                          store
                          "build-1"
                          workspace
                          "links"
                          [ "ordinary.txt"; pattern ]
                          []
                          useDefaults
                          (fun () -> false)
                  with
                  | Ok result -> failtestf "selected symlinks were copied: %A" result
                  | Error problem ->
                      Expect.equal
                          problem.Describe
                          $"stash refuses selected path ‘{refusedPath}’: selected symbolic links and linked directory descendants are not stashed"
                          $"{shape}: the refusal names the exact selected path and stable link policy"

                  Expect.equal
                      (inventory store.Root)
                      before
                      "a refused replacement publishes no sentinel and preserves the complete prior stash"
                  Expect.equal
                      (before |> List.map snd)
                      [ "ordinary" ]
                      "the ordinary control was copied through the descriptor boundary"
          }

          testList "FG-228 linked-directory traversal mutant" [
            test "selected directory link traversal is refused before copy" {
              let root = tempRoot ()
              let workspace, pattern, refusedPath = arrange root "out-dir"
              let store = StashStore.under (Path.Combine(root, "controller"))

              match
                  Stash.save
                      store
                      "build-1"
                      workspace
                      "directory-link"
                      [ pattern ]
                      []
                      false
                      (fun () -> false)
              with
              | Ok result -> failtestf "selected directory symlink was copied: %A" result
              | Error problem ->
                  Expect.equal
                      problem.Describe
                      $"stash refuses selected path ‘{refusedPath}’: selected symbolic links and linked directory descendants are not stashed"
                      "the directory link is rejected by classification before its target is entered"

              let cancellationRoot = tempRoot ()
              let cancellationWorkspace, cancellationPattern, _ = arrange cancellationRoot "out-dir"
              let cancellationStore = StashStore.under (Path.Combine(cancellationRoot, "controller"))
              File.Delete(Path.Combine(cancellationWorkspace, "ordinary.txt"))
              let mutable polls = 0
              let abortAfterSelection () =
                  polls <- polls + 1
                  polls >= 3

              match
                  Stash.save
                      cancellationStore
                      "build-1"
                      cancellationWorkspace
                      "cancelled-directory-link"
                      [ cancellationPattern ]
                      []
                      false
                      abortAfterSelection
              with
              | Error problem -> failtestf "selection refusal incorrectly outranked cancellation: %s" problem.Describe
              | Ok(saved, aborted) ->
                  Expect.isEmpty saved "nothing is published when cancellation follows selection"
                  Expect.isTrue aborted "cancellation remains authoritative over the simultaneous refusal"

              Expect.equal polls 3 "the final poll observes cancellation only after selection records the link refusal"
            } ]

          test "unselected, unmatched and wholly excluded directory links remain inert" {
              for pattern, excludes in
                  [ "other/**", []
                    "link", []
                    "link/", []
                    "link//child", []
                    "link/value.txt", [ "link/value.txt" ]
                    "link/*", [ "link/*" ]
                    "link/**", [ "link/**" ]
                    "link/*.txt", [ "link/*" ]
                    "link/**", [ "**" ]
                    "link/**", [ "link/**/*" ]
                    "link/**", [ "**/?*" ] ] do
                  let root = tempRoot ()
                  let workspace = Path.Combine(root, "workspace")
                  let outside = Path.Combine(root, "outside")
                  let store = StashStore.under (Path.Combine(root, "controller"))
                  Directory.CreateDirectory workspace |> ignore
                  write (Path.Combine(outside, "value.txt")) "outside"
                  Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), outside) |> ignore

                  match Stash.save store "build-1" workspace "inert" [ pattern ] excludes false (fun () -> false) with
                  | Error problem -> failtestf "%s unexpectedly selected the link: %s" pattern problem.Describe
                  | Ok(saved, false) -> Expect.isEmpty saved $"{pattern}: no ordinary file matched"
                  | Ok(_, true) -> failtest "an inert non-cancelled save reported cancellation"

              for includePattern, partialExclude in
                  [ "link/**", "link/*/**"
                    "link/**", "link//**"
                    "link/**", "link/**/"
                    "link/*.txt", "link/*.log" ] do
                  let root = tempRoot ()
                  let workspace = Path.Combine(root, "workspace")
                  let outside = Path.Combine(root, "outside")
                  let store = StashStore.under (Path.Combine(root, "controller"))
                  Directory.CreateDirectory workspace |> ignore
                  write (Path.Combine(outside, "value.txt")) "outside"
                  Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), outside) |> ignore

                  match
                      Stash.save
                          store
                          "build-1"
                          workspace
                          "partial-exclude"
                          [ includePattern ]
                          [ partialExclude ]
                          false
                          (fun () -> false)
                  with
                  | Ok result -> failtestf "%s suppressed link refusal: %A" partialExclude result
                  | Error problem ->
                      Expect.stringContains
                          problem.Describe
                          "stash refuses selected path ‘link’"
                          $"{partialExclude} leaves a path admitted by {includePattern}, so the directory link remains selected"

              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let blocked = Path.Combine(workspace, "unselected")
              let store = StashStore.under (Path.Combine(root, "controller"))
              write (Path.Combine(workspace, "selected.txt")) "selected"
              write (Path.Combine(blocked, "unreadable.txt")) "unselected"
              File.SetUnixFileMode(blocked, UnixFileMode.None)

              try
                  match
                      Stash.save
                          store
                          "build-1"
                          workspace
                          "pruned"
                          [ "selected.txt" ]
                          []
                          false
                          (fun () -> false)
                  with
                  | Error problem -> failtestf "an unrelated physical directory was descended: %s" problem.Describe
                  | Ok(saved, false) -> Expect.equal saved [ "selected.txt" ] "only the selected file is copied"
                  | Ok(_, true) -> failtest "an unselected directory caused cancellation"
              finally
                  File.SetUnixFileMode(
                      blocked,
                      UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
          }

          test "directory reachability matches globstar newline and invariant-case regex semantics" {
              let newlineRoot = tempRoot ()
              let newlineWorkspace = Path.Combine(newlineRoot, "workspace")
              let newlineOutside = Path.Combine(newlineRoot, "outside")
              let newlineStore = StashStore.under (Path.Combine(newlineRoot, "controller"))
              Directory.CreateDirectory newlineWorkspace |> ignore
              write (Path.Combine(newlineOutside, "value.txt")) "outside"
              Directory.CreateSymbolicLink(Path.Combine(newlineWorkspace, "linked\nname"), newlineOutside)
              |> ignore

              match
                  Stash.save
                      newlineStore
                      "build-1"
                      newlineWorkspace
                      "newline"
                      [ "**" ]
                      []
                      false
                      (fun () -> false)
              with
              | Error problem -> failtestf "regex-inert LF link was selected: %s" problem.Describe
              | Ok(saved, false) -> Expect.isEmpty saved "dot/globstar does not cross LF"
              | Ok(_, true) -> failtest "an inert LF link reported cancellation"

              let cultureRoot = tempRoot ()
              let cultureWorkspace = Path.Combine(cultureRoot, "workspace")
              let cultureStore = StashStore.under (Path.Combine(cultureRoot, "controller"))
              write (Path.Combine(cultureWorkspace, "i", "value.txt")) "selected"
              let turkish =
                  try Some(Globalization.CultureInfo.GetCultureInfo("tr-TR"))
                  with :? Globalization.CultureNotFoundException -> None

              match turkish with
              | None ->
                  Expect.equal
                      Globalization.CultureInfo.CurrentCulture
                      Globalization.CultureInfo.InvariantCulture
                      "a globalization-invariant runtime already removes ambient-culture disagreement"
              | Some turkish ->
                  let originalCulture = Globalization.CultureInfo.CurrentCulture
                  let originalUiCulture = Globalization.CultureInfo.CurrentUICulture

                  try
                      Globalization.CultureInfo.CurrentCulture <- turkish
                      Globalization.CultureInfo.CurrentUICulture <- turkish

                      match
                          Stash.save
                              cultureStore
                              "build-1"
                              cultureWorkspace
                              "culture"
                              [ "I/**" ]
                              []
                              false
                              (fun () -> false)
                      with
                      | Error problem -> failtest problem.Describe
                      | Ok(saved, false) ->
                          Expect.equal
                              saved
                              [ "i/value.txt" ]
                              "regex leaf matching and NFA descent both use invariant folding"
                      | Ok(_, true) -> failtest "the invariant culture control reported cancellation"
                  finally
                      Globalization.CultureInfo.CurrentCulture <- originalCulture
                      Globalization.CultureInfo.CurrentUICulture <- originalUiCulture
          }

          test "directory reachability bounds aggregate transitions and polls cancellation" {
              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let outside = Path.Combine(root, "outside")
              let store = StashStore.under (Path.Combine(root, "controller"))
              Directory.CreateDirectory workspace |> ignore
              write (Path.Combine(outside, "value.txt")) "outside"
              Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), outside) |> ignore

              // Each distinct literal expands the NFA search alphabet. The
              // selection-wide transition budget keeps this adversarial but
              // admitted pattern bounded, while the first in-search poll makes
              // cancellation authoritative over the conservative link refusal.
              let suffix =
                  [| for code in 0x1000 .. 0x17ff -> char code |]
                  |> String
              let pattern = "link/**/" + suffix
              let mutable polls = 0
              let abortDuringSearch () =
                  polls <- polls + 1
                  polls >= 3
              let cancelledWatch = Diagnostics.Stopwatch.StartNew()

              match
                  Stash.save
                      store
                      "build-1"
                      workspace
                      "cancelled-search"
                      [ pattern ]
                      []
                      false
                      abortDuringSearch
              with
              | Error problem -> failtestf "search cancellation lost to refusal: %s" problem.Describe
              | Ok(saved, aborted) ->
                  Expect.isEmpty saved "a cancelled search publishes no files"
                  Expect.isTrue aborted "the reachability search observes cancellation"

              cancelledWatch.Stop()
              Expect.equal polls 3 "the third poll occurs inside glob reachability"
              Expect.isLessThan cancelledWatch.ElapsedMilliseconds 5_000L "cancellation is observed promptly"

              let boundedWatch = Diagnostics.Stopwatch.StartNew()

              match
                  Stash.save
                      store
                      "build-1"
                      workspace
                      "bounded-search"
                      [ pattern ]
                      []
                      false
                      (fun () -> false)
              with
              | Ok result -> failtestf "budget exhaustion permitted a linked directory: %A" result
              | Error problem ->
                  Expect.stringContains
                      problem.Describe
                      "stash refuses selected path ‘link’"
                      "budget exhaustion fails closed at the selected link"

              boundedWatch.Stop()
              Expect.isLessThan boundedWatch.ElapsedMilliseconds 5_000L "aggregate transition work is bounded"
          }

          test "directory descent is bound to descriptors across pathname replacement" {
              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let childPath = Path.Combine(workspace, "child")
              let movedPath = Path.Combine(workspace, "checked-child")
              let outside = Path.Combine(root, "outside")
              write (Path.Combine(childPath, "value.txt")) "inside"
              write (Path.Combine(outside, "value.txt")) "outside"

              match Native.openDirectoryWithoutLinks workspace with
              | Error why -> failtest why
              | Ok workspaceDescriptor ->
                  use workspaceDescriptor = workspaceDescriptor

                  match Native.openChildDirectoryWithoutLinks workspaceDescriptor "child" with
                  | Error why -> failtest why
                  | Ok childDescriptor ->
                      use childDescriptor = childDescriptor
                      Directory.Move(childPath, movedPath)
                      Directory.CreateSymbolicLink(childPath, outside) |> ignore

                      match Native.openChildDirectoryWithoutLinks workspaceDescriptor "child" with
                      | Ok escaped ->
                          escaped.Dispose()
                          failtest "the replacement directory link was reopened"
                      | Error why ->
                          Expect.stringContains why "linked" "O_NOFOLLOW rejects the swapped directory"

                      let values =
                          Directory.GetFiles(Native.directoryDescriptorPath childDescriptor)
                          |> Array.map File.ReadAllText
                          |> Array.toList

                      Expect.equal
                          values
                          [ "inside" ]
                          "the live child descriptor remains on the checked physical directory"
          }

          test "an absent scan root beneath a dangling link is refused rather than treated as empty" {
              let root = tempRoot ()
              let missingTarget = Path.Combine(root, "missing")
              let linkedAncestor = Path.Combine(root, "link")
              let workspace = Path.Combine(linkedAncestor, "sub")
              let store = StashStore.under (Path.Combine(root, "controller"))
              Directory.CreateSymbolicLink(linkedAncestor, missingTarget) |> ignore

              match Stash.save store "build-1" workspace "empty" [ "**" ] [] true (fun () -> false) with
              | Ok saved -> failtestf "dangling-ancestor scan was treated as empty: %A" saved
              | Error problem ->
                  Expect.stringContains
                      problem.Describe
                      "ancestor is linked or unavailable"
                      "component-wise O_NOFOLLOW distinguishes the dangling ancestor from honest absence"
          }

          test "an opened source descriptor survives pathname replacement while a path-copy mutant follows it" {
              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let sourcePath = Path.Combine(workspace, "source.txt")
              let movedPath = Path.Combine(workspace, "original.txt")
              let outside = Path.Combine(root, "outside.txt")
              Directory.CreateDirectory workspace |> ignore
              File.WriteAllText(sourcePath, "descriptor-original")
              File.WriteAllText(outside, "path-mutant-external")

              match Native.openFileWithoutLinks workspace "source.txt" with
              | Error why -> failtest why
              | Ok stream ->
                  use stream = stream
                  File.Move(sourcePath, movedPath)
                  File.CreateSymbolicLink(sourcePath, outside) |> ignore
                  use reader = new StreamReader(stream)
                  Expect.equal
                      (reader.ReadToEnd())
                      "descriptor-original"
                      "the production stream stays bound to the inode opened before replacement"
                  Expect.equal
                      (File.ReadAllText sourcePath)
                      "path-mutant-external"
                      "the planted pathname-copy mutant follows the replacement link"
          }

          test "unstash refuses final and ancestor destination links without overwriting external canaries" {
              for nested in [ false; true ] do
                  let root = tempRoot ()
                  let workspace = Path.Combine(root, "workspace")
                  let store = StashStore.under (Path.Combine(root, "controller"))
                  Directory.CreateDirectory workspace |> ignore
                  let relative = if nested then "nested/value.txt" else "value.txt"
                  write (Path.Combine(workspace, relative)) "saved"

                  match Stash.save store "build-1" workspace "safe" [ relative ] [] true (fun () -> false) with
                  | Error problem -> failtest problem.Describe
                  | Ok _ -> ()

                  Directory.Delete(workspace, true)
                  Directory.CreateDirectory workspace |> ignore
                  let canary = Path.Combine(root, if nested then "nested-canary" else "file-canary")

                  if nested then
                      Directory.CreateDirectory canary |> ignore
                      write (Path.Combine(canary, "value.txt")) "outside-unchanged"
                      Directory.CreateSymbolicLink(Path.Combine(workspace, "nested"), canary) |> ignore
                  else
                      File.WriteAllText(canary, "outside-unchanged")
                      File.CreateSymbolicLink(Path.Combine(workspace, "value.txt"), canary) |> ignore

                  match Stash.restore store "build-1" workspace "safe" (fun () -> false) with
                  | Ok restored -> failtestf "unsafe destination restored: %A" restored
                  | Error why ->
                      Expect.stringContains why relative "the refusal names the restore path"

                  let canaryFile = if nested then Path.Combine(canary, "value.txt") else canary
                  Expect.equal (File.ReadAllText canaryFile) "outside-unchanged" "unstash never overwrites outside"
          }

          test "unstash descriptor-walk materializes an absent logical workspace" {
              let root = tempRoot ()
              let sourceWorkspace = Path.Combine(root, "source")
              let missingWorkspace = Path.Combine(root, "logical", "new")
              let store = StashStore.under (Path.Combine(root, "controller"))
              write (Path.Combine(sourceWorkspace, "nested", "value.txt")) "saved"

              match
                  Stash.save
                      store
                      "build-1"
                      sourceWorkspace
                      "safe"
                      [ "nested/value.txt" ]
                      []
                      true
                      (fun () -> false)
              with
              | Error problem -> failtest problem.Describe
              | Ok _ -> ()

              match Stash.restore store "build-1" missingWorkspace "safe" (fun () -> false) with
              | Error why -> failtest why
              | Ok restored ->
                  Expect.equal restored [ "nested/value.txt" ] "the requested stash is restored"

              Expect.equal
                  (File.ReadAllText(Path.Combine(missingWorkspace, "nested", "value.txt")))
                  "saved"
                  "missing logical root and stored parents are created without following links"
          }

          test "unstash refuses a linked controller source without disclosing its target" {
              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let store = StashStore.under (Path.Combine(root, "controller"))
              Directory.CreateDirectory workspace |> ignore
              File.WriteAllText(Path.Combine(workspace, "value.txt"), "saved")

              match Stash.save store "build-1" workspace "safe" [ "value.txt" ] [] true (fun () -> false) with
              | Error problem -> failtest problem.Describe
              | Ok _ -> ()

              let stored = Directory.GetFiles(store.Root, "value.txt", SearchOption.AllDirectories) |> Array.exactlyOne
              let outside = Path.Combine(root, "controller-canary.txt")
              File.WriteAllText(outside, "controller-secret")
              File.Delete stored
              File.CreateSymbolicLink(stored, outside) |> ignore
              File.Delete(Path.Combine(workspace, "value.txt"))

              match Stash.restore store "build-1" workspace "safe" (fun () -> false) with
              | Ok restored -> failtestf "linked source restored: %A" restored
              | Error why ->
                  Expect.equal
                      why
                      "unstash refuses stored path ‘value.txt’: stored symbolic links and linked directory descendants are not restored"
                      "stored-link refusal uses the stable unstash-specific diagnostic"

              Expect.isFalse
                  (File.Exists(Path.Combine(workspace, "value.txt")))
                  "controller target bytes never enter the workspace"

              let mutable polls = 0
              let abortAfterSelection () =
                  polls <- polls + 1
                  polls >= 3

              match Stash.restore store "build-1" workspace "safe" abortAfterSelection with
              | Ok restored -> failtestf "cancelled linked source restored: %A" restored
              | Error why ->
                  Expect.equal
                      why
                      "aborted: the step was interrupted while restoring the stash"
                      "cancellation outranks the simultaneously recorded stored-link refusal"

              Expect.equal polls 3 "the final post-selection poll observes cancellation"
          }

          test "executable and special mode bits survive stash and unstash" {
              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let source = Path.Combine(workspace, "run.sh")
              let store = StashStore.under (Path.Combine(root, "controller"))
              let mode =
                  UnixFileMode.UserRead
                  ||| UnixFileMode.UserWrite
                  ||| UnixFileMode.UserExecute
                  ||| UnixFileMode.GroupRead
                  ||| UnixFileMode.GroupExecute
                  ||| UnixFileMode.SetUser
                  ||| UnixFileMode.SetGroup
              write source "#!/bin/sh\nprintf preserved\n"
              File.SetUnixFileMode(source, mode)

              match Stash.save store "build-1" workspace "mode" [ "run.sh" ] [] true (fun () -> false) with
              | Error problem -> failtest problem.Describe
              | Ok _ -> ()

              Directory.Delete(workspace, true)
              Directory.CreateDirectory workspace |> ignore

              match Stash.restore store "build-1" workspace "mode" (fun () -> false) with
              | Error why -> failtest why
              | Ok restored -> Expect.equal restored [ "run.sh" ] "the executable was restored"

              Expect.equal
                  (File.GetUnixFileMode source)
                  mode
                  "descriptor-relative restore preserves the source permission bits"
          }

          test "modification time survives stash and unstash" {
              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let source = Path.Combine(workspace, "old-report.xml")
              let store = StashStore.under (Path.Combine(root, "controller"))
              let modified = DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc)
              write source "<testsuite/>"
              File.SetLastWriteTimeUtc(source, modified)

              match Stash.save store "build-1" workspace "timestamp" [ "old-report.xml" ] [] true (fun () -> false) with
              | Error problem -> failtest problem.Describe
              | Ok _ -> ()

              let stored =
                  Directory.GetFiles(store.Root, "old-report.xml", SearchOption.AllDirectories)
                  |> Array.exactlyOne
              Expect.equal
                  (File.GetLastWriteTimeUtc stored)
                  modified
                  "the descriptor-bound staging copy preserves the source timestamp"

              Directory.Delete(workspace, true)
              Directory.CreateDirectory workspace |> ignore

              match Stash.restore store "build-1" workspace "timestamp" (fun () -> false) with
              | Error why -> failtest why
              | Ok _ -> ()

              Expect.equal
                  (File.GetLastWriteTimeUtc source)
                  modified
                  "descriptor-bound restore preserves the stored timestamp"
          }

          test "staging creation failure returns a structured storage problem" {
              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let controllerFile = Path.Combine(root, "controller-file")
              let store = StashStore.under controllerFile
              write (Path.Combine(workspace, "value.txt")) "saved"
              File.WriteAllText(controllerFile, "not-a-directory")

              match Stash.save store "build-1" workspace "broken" [ "**" ] [] true (fun () -> false) with
              | Ok saved -> failtestf "invalid controller storage unexpectedly published: %A" saved
              | Error problem ->
                  Expect.stringContains
                      problem.Describe
                      "could not create staged stash"
                      "controller I/O failure stays inside the typed save boundary"
          }

          test "cancellation cleanup failure cannot escape the typed save result" {
              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let store = StashStore.under (Path.Combine(root, "controller"))
              let writable =
                  UnixFileMode.UserRead
                  ||| UnixFileMode.UserWrite
                  ||| UnixFileMode.UserExecute
              let readOnly = UnixFileMode.UserRead ||| UnixFileMode.UserExecute
              let mutable lockedParent: string option = None
              write (Path.Combine(workspace, "value.txt")) "saved"

              let abort () =
                  if not (Directory.Exists store.Root) then
                      false
                  else
                      match Directory.GetDirectories(store.Root, "*.new-*", SearchOption.AllDirectories) with
                      | [||] -> false
                      | candidates ->
                          let parent = Directory.GetParent(candidates.[0]).FullName
                          File.SetUnixFileMode(parent, readOnly)
                          lockedParent <- Some parent
                          true

              try
                  match Stash.save store "build-1" workspace "cancel" [ "**" ] [] true abort with
                  | Error problem -> failtest problem.Describe
                  | Ok(saved, aborted) ->
                      Expect.isEmpty saved "cancellation preceded the first staged copy"
                      Expect.isTrue aborted "the cooperative cancellation remains the result"
              finally
                  lockedParent |> Option.iter (fun parent -> File.SetUnixFileMode(parent, writable))
          }

          test "post-copy cancellation reports no files from the discarded staging tree" {
              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let store = StashStore.under (Path.Combine(root, "controller"))
              write (Path.Combine(workspace, "value.txt")) "staged but never published"

              let abortAfterStagedCopy () =
                  Directory.Exists store.Root
                  && Directory.GetFiles(store.Root, "value.txt", SearchOption.AllDirectories).Length = 1

              match
                  Stash.save
                      store
                      "build-1"
                      workspace
                      "cancel-after-copy"
                      [ "**" ]
                      []
                      true
                      abortAfterStagedCopy
              with
              | Error problem -> failtest problem.Describe
              | Ok(saved, aborted) ->
                  Expect.isEmpty saved "discarded staging files are not reported as committed"
                  Expect.isTrue aborted "the post-copy cancellation remains the result"

              Expect.isEmpty
                  (Directory.GetFiles(store.Root, "*", SearchOption.AllDirectories))
                  "cancellation removes the staged file instead of publishing it"
          }

          test "diagnostic path rendering cannot forge a second console line" {
              let problem =
                  Stash.SaveProblem.SelectedPathRefused("bad\nERROR: forged", "symbolic link")

              Expect.equal
                  problem.Describe
                  "stash refuses selected path ‘bad\\u000AERROR: forged’: symbolic link"
                  "control characters are escaped in the stable operator diagnostic"
          }

          test "unstash restore diagnostics escape stored control characters" {
              let root = tempRoot ()
              let workspace = Path.Combine(root, "workspace")
              let store = StashStore.under (Path.Combine(root, "controller"))
              // `**` does not select LF-containing names under .NET's default
              // regex mode. CR is still a console-control boundary and reaches
              // the descriptor-relative restore refusal end to end.
              let relative = "bad\rERROR: forged"
              let outside = Path.Combine(root, "outside.txt")
              Directory.CreateDirectory workspace |> ignore
              File.WriteAllText(Path.Combine(workspace, relative), "saved")

              match Stash.save store "build-1" workspace "diagnostic" [ relative ] [] true (fun () -> false) with
              | Error problem -> failtest problem.Describe
              | Ok _ -> ()

              File.WriteAllText(outside, "outside-unchanged")
              File.Delete(Path.Combine(workspace, relative))
              File.CreateSymbolicLink(Path.Combine(workspace, relative), outside) |> ignore

              match Stash.restore store "build-1" workspace "diagnostic" (fun () -> false) with
              | Ok restored -> failtestf "unsafe destination restored: %A" restored
              | Error why ->
                  Expect.isFalse (why.Contains '\n') "the diagnostic stays on one console line"
                  Expect.isFalse (why.Contains '\r') "the diagnostic cannot return to the line start"
                  Expect.stringContains
                      why
                      "bad\\u000DERROR: forged"
                      "the outer restore path is escaped before rendering"

              Expect.equal (File.ReadAllText outside) "outside-unchanged" "the linked target remains untouched"
          } ]

    if OperatingSystem.IsLinux() then
        testList "FG-228 stash symlink containment" tests
    else
        ptestList "FG-228 stash symlink containment" tests

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

          test "a binary file credential masks base64, detects hex, and reports no phantom literal leak" {
              // REVIEW FIX (Codex, PR #15 round 5): `bindBytes` stores an empty Value for a
              // non-UTF-8 credential, and every string contains the empty string — so a
              // literal leak was reported on EVERY line of output inside the block. A
              // warning that always fires trains the reader to ignore the channel.
              let root = tempRoot ()
              let bytes = [| 0uy; 159uy; 146uy; 150uy; 255uy; 1uy; 2uy; 3uy |] // invalid UTF-8
              let binding = Secrets.bindBytes root "CERT" bytes
              let base64 = Convert.ToBase64String bytes
              let hexLower = Convert.ToHexString(bytes).ToLowerInvariant()
              let hexUpper = Convert.ToHexString bytes
              Array.fill bytes 0 bytes.Length 0uy

              Expect.isEmpty (Secrets.detectLeaks [ binding ] "ordinary build output") "no phantom leak"
              Expect.equal (Secrets.mask [ binding ] "ordinary build output") "ordinary build output" "output untouched"
              Expect.equal (Secrets.mask [ binding ] $"encoded={base64}") "encoded=****" "raw-byte base64 is masked"

              let leaks = Secrets.detectLeaks [ binding ] $"lower={hexLower} upper={hexUpper}"
              Expect.equal
                  (leaks |> List.map (fun leak -> leak.Encoding))
                  [ "hex"; "hex-upper" ]
                  "both common raw-byte hex forms are reported"

              Secrets.revoke [ binding ]
          }

          test "short or low-diversity binary encodings cannot false-refuse ordinary output" {
              let root = tempRoot ()
              let bytes = [| 0xDEuy; 0xADuy |]
              let binding = Secrets.bindBytes root "CERT" bytes
              let base64 = Convert.ToBase64String bytes
              let sevenBytes = [| 0uy; 159uy; 146uy; 150uy; 255uy; 1uy; 2uy |]
              let sevenByteBinding = Secrets.bindBytes root "CERT_SEVEN" sevenBytes
              let sevenByteBase64 = Convert.ToBase64String sevenBytes
              let sevenByteHex = Convert.ToHexString sevenBytes
              let repeatedBytes = Array.zeroCreate<byte> 8
              let repeatedBinding = Secrets.bindBytes root "CERT_REPEATED" repeatedBytes
              let repeatedBase64 = Convert.ToBase64String repeatedBytes
              let repeatedHex = Convert.ToHexString repeatedBytes
              let threeDistinct = [| 0xFFuy; 0xFEuy; 0xFDuy; 0xFFuy; 0xFEuy; 0xFDuy; 0xFFuy; 0xFEuy |]
              let threeDistinctBinding = Secrets.bindBytes root "CERT_THREE" threeDistinct
              let threeDistinctHex = Convert.ToHexString threeDistinct
              let fourDistinct = [| 0xFFuy; 0xFEuy; 0xFDuy; 0xFCuy; 0xFFuy; 0xFEuy; 0xFDuy; 0xFCuy |]
              let fourDistinctBinding = Secrets.bindBytes root "CERT_FOUR" fourDistinct
              let fourDistinctHex = Convert.ToHexString fourDistinct

              Expect.equal
                  (Secrets.mask [ binding ] $"word=dead upper=DEAD encoded={base64}")
                  $"word=dead upper=DEAD encoded={base64}"
                  "low-entropy binary forms below eight bytes are not registered"

              Expect.isEmpty
                  (Secrets.detectLeaks [ binding ] "word=dead upper=DEAD")
                  "ordinary four-letter words do not become credential-leak proof"

              Expect.equal
                  (Secrets.mask [ sevenByteBinding ] $"encoded={sevenByteBase64}")
                  $"encoded={sevenByteBase64}"
                  "the byte-derived floor excludes a seven-byte value"

              Expect.isEmpty
                  (Secrets.detectLeaks [ sevenByteBinding ] $"encoded={sevenByteHex}")
                  "the exact byte-derived floor excludes seven-byte hex"

              Expect.equal
                  (Secrets.mask [ repeatedBinding ] $"encoded={repeatedBase64}")
                  "encoded=****"
                  "non-terminal exact-base64 masking remains available at the length floor"

              Expect.isEmpty
                  (Secrets.detectLeaks [ repeatedBinding ] $"encoded={repeatedHex}")
                  "eight repeated bytes do not become credential-leak proof"

              Expect.isEmpty
                  (Secrets.detectLeaks [ threeDistinctBinding ] $"encoded={threeDistinctHex}")
                  "three distinct bytes remain below the terminal detection floor"

              Expect.isNonEmpty
                  (Secrets.detectLeaks [ fourDistinctBinding ] $"encoded={fourDistinctHex}")
                  "four distinct bytes cross the exact terminal detection floor"

              Secrets.revoke
                  [ binding
                    sevenByteBinding
                    repeatedBinding
                    threeDistinctBinding
                    fourDistinctBinding ]
          }

          test "binary forms are prepared lazily once and shared by repeated bindings" {
              let root = tempRoot ()
              let bytes = [| 0uy; 159uy; 146uy; 150uy; 255uy; 1uy; 2uy; 3uy |]
              let credential = Secrets.prepareFileCredential "secret.dat" bytes
              Expect.isFalse credential.FormsCreated "store resolution does not derive unused encodings"
              let first = Secrets.bindPreparedFile root "CERT_A" credential
              Expect.isTrue credential.FormsCreated "the first requested binding derives its forms"
              let second = Secrets.bindPreparedFile root "CERT_B" credential

              Expect.isTrue
                  (Object.ReferenceEquals(first.Forms, second.Forms))
                  "lexical bindings share the source credential's immutable encodings"

              Secrets.revoke [ first; second ]
          }

          test "hostile variable names never enter the secret-file path" {
              let root = tempRoot ()
              let binding = Secrets.bind root "../hostile/name" "SUPERSECRET123"
              let expectedParent = Path.GetFullPath root
              let actualParent = FileInfo(binding.FilePath).Directory.FullName

              Expect.equal actualParent expectedParent "the companion remains directly below its secret root"
              Expect.stringStarts (Path.GetFileName binding.FilePath) ".secret-" "the leaf uses the opaque prefix"
              Expect.isFalse
                  ((Path.GetFileName binding.FilePath).Contains "hostile")
                  "the Jenkinsfile-controlled variable is absent from the leaf"

              Secrets.revoke [ binding ]
          }

          test "invalid environment keys neither create files nor force prepared forms" {
              let root = tempRoot ()

              for hostile in [ ""; "BAD=NAME"; "BAD" + String('\000', 1) + "NAME" ] do
                  Expect.throwsT<ArgumentException>
                      (fun () -> Secrets.inMemoryTextBinding hostile "SUPERSECRET123" |> ignore)
                      $"in-memory binding rejects {hostile.Length}-character invalid key"

                  Expect.throwsT<ArgumentException>
                      (fun () -> Secrets.bind root hostile "SUPERSECRET123" |> ignore)
                      $"text binding rejects {hostile.Length}-character invalid key"

                  let prepared =
                      Secrets.prepareFileCredential
                          "secret.dat"
                          [| 0xFFuy; 0xFEuy; 0xFDuy; 0xFCuy; 0xFFuy; 0xFEuy; 0xFDuy; 0xFCuy |]

                  Expect.throwsT<ArgumentException>
                      (fun () -> Secrets.bindPreparedFile root hostile prepared |> ignore)
                      $"prepared binding rejects {hostile.Length}-character invalid key"
                  Expect.isFalse prepared.FormsCreated "validation happens before lazy form derivation"

              let files =
                  if Directory.Exists root then Directory.GetFiles(root, "*", SearchOption.AllDirectories) else [||]

              Expect.isEmpty files "invalid keys never materialize a companion file"
          }

          test "prepared file credentials snapshot caller bytes with their forms" {
              let root = tempRoot ()
              let original = [| 0uy; 159uy; 146uy; 150uy; 255uy; 1uy; 2uy; 3uy |]
              let expected = Array.copy original
              let credential = Credentials.secretFile "secret.dat" original
              Array.fill original 0 original.Length 0x41uy

              match credential with
              | SecretFile prepared ->
                  let binding = Secrets.bindPreparedFile root "CERT" prepared
                  let base64 = Convert.ToBase64String expected
                  let hexUpper = Convert.ToHexString expected

                  Expect.sequenceEqual
                      (File.ReadAllBytes binding.FilePath)
                      expected
                      "the materialized file uses the constructor snapshot"

                  Expect.stringContains
                      (Secrets.mask [ binding ] $"encoded={base64}")
                      "encoded=****"
                      "masking forms describe the same snapshot"

                  Expect.isNonEmpty
                      (Secrets.detectLeaks [ binding ] $"encoded={hexUpper}")
                      "detection forms describe the same snapshot"

                  Secrets.revoke [ binding ]
              | other -> failtestf "file factory produced %A" other
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

              let preservingOuter =
                  Secrets.environmentForPreserving (Set.ofList [ "TOKEN_FILE" ]) [ explicitBinding; other ]
                  |> Map.ofList

              Expect.equal
                  (Map.tryFind "TOKEN_FILE" preservingOuter)
                  (Some "explicit-secret")
                  "a current explicit value still shadows a preserved outer name"

              Secrets.revoke [ explicitBinding; other ]
          }

          test "generated companions preserve exact outer names and keep unused names" {
              let root = tempRoot ()
              let token = Secrets.bind root "TOKEN" "text-secret"
              let user = Secrets.bind root "USER" "measured-user"
              let cert = Secrets.bindBytes root "CERT" (Text.Encoding.UTF8.GetBytes "certificate")

              let actual =
                  Secrets.environmentForPreserving
                      (Set.ofList [ "TOKEN_FILE"; "USER_FILE"; "cert_file" ])
                      [ token; user; cert ]

              Expect.equal
                  actual
                  [ "TOKEN", "text-secret"
                    "USER", "measured-user"
                    "CERT", cert.FilePath
                    "CERT_FILE", cert.FilePath ]
                  "values stay lexical, exact protected companions disappear, and an unused companion remains"

              Expect.equal
                  (Secrets.environmentForPreserving (Set.ofList [ "CERT_FILE" ]) [ cert ])
                  [ "CERT", cert.FilePath ]
                  "an exact protected companion is suppressed for a binary file-style binding too"

              Expect.equal
                  (Secrets.environmentForPreserving (Set.ofList [ "token_file" ]) [ token ])
                  [ "TOKEN", "text-secret"; "TOKEN_FILE", token.FilePath ]
                  "environment names remain case-sensitive"

              Expect.equal
                  (Secrets.environmentFor [ token; user; cert ])
                  (Secrets.environmentForPreserving Set.empty [ token; user; cert ])
                  "the legacy no-outer-environment entry point is byte-for-byte equivalent"

              Secrets.revoke [ token; user; cert ]
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
                  let saved, _ =
                      match Stash.save store "build-1" ws hostile [ "f.txt" ] [] true (fun () -> false) with
                      | Ok result -> result
                      | Error problem -> failtest problem.Describe
                  Expect.equal saved [ "f.txt" ] $"the stash still works for name '{hostile}'"

              Expect.isTrue (File.Exists canary) "nothing outside the stash root was touched"
              Expect.equal (File.ReadAllText canary) "must survive" "and it was not overwritten"

              // Distinct hostile names must not collide with each other either.
              let save name =
                  match Stash.save store "build-1" ws name [ "f.txt" ] [] true (fun () -> false) with
                  | Ok result -> result
                  | Error problem -> failtest problem.Describe
              let a, _ = save "x"
              let b, _ = save "y"
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

          test "text and binary secret files are created at their final owner-only mode" {
              let root = tempRoot ()
              let ws = match Workspace.createFresh root (key ()) with
                       | Result.Ok p -> p
                       | Result.Error e -> failtestf "%s" e.Describe

              let text = Secrets.bind ws "TOKEN" "SUPERSECRET123"
              let binaryBytes = [| 0uy; 255uy; 13uy; 10uy |]
              let binary = Secrets.bindBytes ws "CERT" binaryBytes

              for label, binding in [ "text", text; "binary", binary ] do
                  let mode = File.GetUnixFileMode binding.FilePath
                  Expect.equal
                      mode
                      (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                      $"{label}: exact final mode is 0600"

              Expect.equal (File.ReadAllText text.FilePath) "SUPERSECRET123" "text bytes are exact"
              Expect.equal (File.ReadAllBytes binary.FilePath) binaryBytes "binary bytes are exact"
              Secrets.revoke [ text; binary ]
          }

          test "secret files are mode 0600 and empty before the first byte is written" {
              let root = tempRoot ()
              let observations = Collections.Generic.List<_>()

              let path =
                  Secrets.createSecretFileWithObserver
                      root
                      (Text.Encoding.UTF8.GetBytes "SUPERSECRET123")
                      (fun phase path ->
                          observations.Add(phase, File.GetUnixFileMode path, FileInfo(path).Length))

              Expect.sequenceEqual
                  (observations |> Seq.map (fun (phase, _, _) -> phase))
                  [ Secrets.SecretFilePhase.Opened; Secrets.SecretFilePhase.ReadyToWrite ]
                  "the descriptor is tightened before the write boundary"

              let _, readyMode, readyLength = observations.[1]
              Expect.equal
                  readyMode
                  (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                  "the descriptor is exactly 0600 at the pre-write boundary"
              Expect.equal readyLength 0L "no secret byte exists before the descriptor is tightened"
              Expect.equal (File.ReadAllText path) "SUPERSECRET123" "the write follows the observed boundary"
              File.Delete path
          }

          test "secret creation refuses an existing path without truncating it" {
              let root = tempRoot ()
              let path = Path.Combine(root, ".secret-planted-collision")
              let canary = "existing-secret-canary"
              File.WriteAllText(path, canary)
              let mutable observed = false

              Expect.throwsT<IOException>
                  (fun () ->
                      Secrets.writeSecretFileAtPathWithObserver
                          path
                          (Text.Encoding.UTF8.GetBytes "replacement-secret")
                          (fun _ _ -> observed <- true)
                      |> ignore)
                  "CreateNew refuses to overwrite a stale or colliding secret path"

              Expect.isFalse observed "creation failed before exposing an opened descriptor"
              Expect.equal (File.ReadAllText path) canary "the existing file was not truncated or replaced"
              File.Delete path
          }

          test "a restrictive umask cannot remove owner readability from a secret" {
              let report = Path.Combine(tempRoot (), "secret-umask.report")
              // Pre-create the diagnostic outside the child's restrictive umask;
              // WriteAllText then truncates without changing its mode.
              File.WriteAllText(report, "")
              let start = ProcessStartInfo("/bin/sh")
              start.ArgumentList.Add "-c"
              start.ArgumentList.Add "umask 0400; exec \"$@\""
              start.ArgumentList.Add "fogell-secret-umask"
              start.ArgumentList.Add Environment.ProcessPath

              if Path.GetFileNameWithoutExtension(Environment.ProcessPath) = "dotnet" then
                  start.ArgumentList.Add(Reflection.Assembly.GetExecutingAssembly().Location)

              start.ArgumentList.Add "--secret-umask-child"
              start.ArgumentList.Add report
              start.UseShellExecute <- false
              start.RedirectStandardOutput <- true
              start.RedirectStandardError <- true
              use child = Process.Start start
              let stdout = child.StandardOutput.ReadToEnd()
              let stderr = child.StandardError.ReadToEnd()
              child.WaitForExit()
              let reportText = if File.Exists report then File.ReadAllText report else "<missing>"

              Expect.equal
                  child.ExitCode
                  0
                  $"restrictive-umask child succeeds; stdout={stdout}; stderr={stderr}; report={reportText}"
              Expect.equal
                  (File.ReadAllText report)
                  "384|SUPERSECRET123"
                  "the child observed exact 0600 and readable content"
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
                        JUnitSkipMarkingBuildUnstable = false
                        JUnitAllowEmptyResults = false
                        JUnitSkipOldReportsSince = None
                        JUnitSkipMarkingStageUnstable = false
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
              Expect.isNone r.StageWarning "an interrupted junit does not decorate the stage as unstable"
              Expect.isNone r.TestDuration "an interrupted junit publishes no partial duration"

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
                        JUnitSkipMarkingBuildUnstable = false
                        JUnitAllowEmptyResults = false
                        JUnitSkipOldReportsSince = None
                        JUnitSkipMarkingStageUnstable = false
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
              Expect.isNone noMatch.StageWarning "the zero-match interrupt has no stage warning"
              Expect.isNone noMatch.TestDuration "the zero-match interrupt publishes no duration"

              match noMatch.Diagnostic with
              | Some d -> Expect.isFalse (d.Contains "no test report matched") $"not blamed on the pattern: {d}"
              | None -> failtest "expected a diagnostic"

              match r.Diagnostic with
              | Some d -> Expect.stringContains d "aborted" $"the abort is named: {d}"
              | None -> failtest "an aborted junit must carry a diagnostic"
          }

          test "junit preserves suite authority, pinned parsing, and binary32 addition" {
              let root = tempRoot ()
              let baseRequest = request root ""

              let junit pattern =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          Named = [ "testResults", pattern ] }

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "duration.xml"),
                  "<testsuites time=\"999\">"
                  + "<testsuite name=\"case-derived\"><testcase name=\"a\" time=\"1.25\"/><testcase name=\"b\" time=\"2.5\"/></testsuite>"
                  + "<testsuite name=\"suite-authority\" time=\"4.0\"><testcase name=\"ignored-child-time\" time=\"99\"/></testsuite>"
                  + "</testsuites>")

              let aggregate = junit "duration.xml"
              Expect.equal aggregate.Status Success "duration does not change the report result"
              Expect.equal aggregate.TestTotals (Some(3, 0, 0)) "duration parsing does not change counts"
              Expect.equal aggregate.TestDuration (Some 7.75f) "wrapper time is ignored, child sum and suite override each contribute once"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "width-a.xml"),
                  "<testsuite name=\"wide\" time=\"16777216\"><testcase name=\"wide\"/></testsuite>")
              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "width-b.xml"),
                  "<testsuite name=\"one\" time=\"1\"><testcase name=\"one\"/></testsuite>")

              Expect.equal
                  (junit "width-*.xml").TestDuration
                  (Some 16_777_216.0f)
                  "globally sorted additions round after every JVM-float addition"

              for label, raw, expected in
                  [ "empty", "", Some 0.0f
                    "invalid", "abc", Some 0.0f
                    "negative", "-2", Some 0.0f
                    "nan", "NaN", None
                    "positive-infinity", "+Infinity", Some 31_536_000.0f
                    "comma", "2,503.1", Some 2_503.1f
                    "fallback-lowercase-exponent-stop", "1e2x", Some 1.0f
                    "fallback-uppercase-exponent", "1E2x", Some 100.0f
                    "fallback-uppercase-negative-exponent", "1E-2x", Some 0.01f
                    "fallback-uppercase-positive-sign-stop", "1E+2x", Some 1.0f
                    "fallback-leading-plus", "+1x", Some 0.0f
                    "fallback-leading-space", " 1x", Some 0.0f
                    "fallback-unicode-digits", "١٢x", Some 12.0f
                    "fallback-unicode-exponent", "١E-٢x", Some 0.01f
                    "fallback-combining-mark-stop", "١\u0301٢x", Some 1.0f
                    "fallback-supplementary-digits-refuse", "𝟙𝟚x", Some 0.0f
                    "fallback-double-then-float", "1.000000059604644775390625000001x", Some 1.0f
                    "fallback-integral-width-clamped", "4611686293305294849.0x", Some 31_536_000.0f
                    "fallback-nan-prefix", "NaNx", None
                    "fallback-infinity-prefix", "∞x", Some 31_536_000.0f
                    "fallback-negative-infinity-prefix", "-∞x", Some 0.0f ] do
                  let report = $"duration-{label}.xml"
                  File.WriteAllText(
                      Path.Combine(baseRequest.Workspace, report),
                      $"<testsuite name=\"{label}\" time=\"{raw}\"><testcase name=\"child\" time=\"1.25\"/></testsuite>")
                  let observed = (junit report).TestDuration

                  match expected, observed with
                  | None, Some value -> Expect.isTrue (Single.IsNaN value) "NaN survives Java min/max and summary projection"
                  | Some value, Some actual -> Expect.equal actual value $"{label}: exact pinned TimeToFloat/clamp result"
                  | _ -> failtestf "%s: unexpected duration %A" label observed

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "duration-exponent-overflow.xml"),
                  "<testsuite name=\"exponent-overflow\" time=\"1E2147483647x\"><testcase name=\"child\"/></testsuite>")
              let exponentOverflow = junit "duration-exponent-overflow.xml"
              Expect.equal exponentOverflow.TestTotals (Some(1, 0, 0)) "an oversized fallback exponent does not poison counts"
              Expect.isNone exponentOverflow.TestDuration "oversized DecimalFormat exponent behavior is refused rather than guessed"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "duration-hex.xml"),
                  "<testsuite name=\"hex\" time=\"0x1.0p1\"><testcase name=\"child\"/></testsuite>")
              let hexadecimal = junit "duration-hex.xml"
              Expect.equal hexadecimal.TestTotals (Some(1, 0, 0)) "an unmodelled duration token does not poison counts"
              Expect.isNone hexadecimal.TestDuration "hexadecimal binary32 rounding is refused rather than guessed"
          }

          test "junit build and stage suppression flags cover the measured four-combination matrix" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let report = Path.Combine(baseRequest.Workspace, "report.xml")

              File.WriteAllText(
                  report,
                  "<testsuite name=\"suppression\" tests=\"2\" failures=\"1\" errors=\"0\" skipped=\"0\"><testcase name=\"ok\"/><testcase name=\"bad\"><failure message=\"boom\"/></testcase></testsuite>")

              let junit skipBuild skipStage =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          Named = [ "testResults", "report.xml" ]
                          JUnitSkipMarkingBuildUnstable = skipBuild
                          JUnitSkipMarkingStageUnstable = skipStage }

              // Keep the original build-only control names: comments elsewhere
              // cite these concepts, and the stale-reference audit treats a
              // needless local rename as possible documentation drift.
              let normal = junit false false
              let suppressed = junit true false

              let matrix =
                  [ "skipBuild=false, skipStage=false", normal, Unstable, Some Unstable
                    "skipBuild=true, skipStage=false", suppressed, Success, Some Unstable
                    "skipBuild=false, skipStage=true", junit false true, Success, None
                    "skipBuild=true, skipStage=true", junit true true, Success, None ]

              for label, observed, expectedStatus, expectedWarning in matrix do

                  Expect.equal observed.Status expectedStatus $"{label}: exact build contribution"
                  Expect.equal observed.StageWarning expectedWarning $"{label}: exact stage decoration"
                  Expect.equal observed.TestTotals (Some(2, 1, 0)) $"{label}: suppression never erases counts"
          }

          test "junit derives totals from direct testcase children through nested suites" {
              let root = tempRoot ()
              let baseRequest = request root ""

              let junit pattern =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          Named = [ "testResults", pattern ] }

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "children.xml"),
                  "<testsuites tests=\"not-a-number\" failures=\"-7\" errors=\"2147483648\" skipped=\"99\">"
                  + "<testsuite name=\"outer\">"
                  + "<testcase name=\"pass\"/>"
                  + "<testcase name=\"failure\"><failure/></testcase>"
                  + "<testcase name=\"both-failures\"><failure/><error/></testcase>"
                  + "<testsuite name=\"inner\" tests=\"999\" failures=\"bad\" errors=\"-1\" skipped=\"88\">"
                  + "<testcase name=\"error\"><error/></testcase>"
                  + "<testcase name=\"skip\"><skipped/></testcase>"
                  + "<testcase name=\"skip-wins\"><failure/><error/><skipped/></testcase>"
                  + "</testsuite>"
                  + "<testsuite name=\"suite-error\"><error message=\"setup failed\"/></testsuite>"
                  + "</testsuite></testsuites>")

              let children = junit "children.xml"
              Expect.equal children.Status Unstable "child failures, not hostile aggregate attributes, mark the build"
              Expect.equal children.TestTotals (Some(7, 4, 2)) "nested direct cases, one suite error, and marker precedence determine exact totals"
              Expect.equal children.StageWarning (Some Unstable) "child-derived failures decorate the stage"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "suite-markers.xml"),
                  "<testsuites>"
                  + "<testsuite name=\"error-then-skipped\"><error/><skipped/></testsuite>"
                  + "<testsuite name=\"skipped-then-error\"><skipped/><error/></testsuite>"
                  + "<testsuite name=\"error-only\"><error/></testsuite>"
                  + "<testsuite name=\"skipped-only\"><skipped/></testsuite>"
                  + "<testsuite name=\"inert-suite-markers\"><failure/><skipped/><testcase name=\"pass\"/></testsuite>"
                  + "</testsuites>")

              let suiteMarkers = junit "suite-markers.xml"
              Expect.equal suiteMarkers.Status Unstable "only the error-only synthetic case fails"
              Expect.equal suiteMarkers.TestTotals (Some(4, 1, 2)) "suite synthetic cases use skipped-first classification in either XML order"
              Expect.equal suiteMarkers.StageWarning (Some Unstable) "the unsuppressed error-only suite decorates the stage"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "mixed-valid.xml"),
                  "<arbitrary><testsuite name=\"real-suite\" tests=\"500\" failures=\"500\"><testcase name=\"only-real-case\"/></testsuite></arbitrary>")
              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "mixed-zero.xml"),
                  "<arbitrary><wrapper><testcase classname=\"ignored.Case\" name=\"not-a-result\"/></wrapper></arbitrary>")

              let mixed = junit "mixed-*.xml"
              Expect.equal mixed.Status Success "a zero-result sibling does not poison an aggregate containing a real case"
              Expect.equal mixed.TestTotals (Some(1, 0, 0)) "arbitrary nested wrappers remain outside the direct-testsuite traversal"
              Expect.isNone mixed.StageWarning "the sole recognized case passed"

              let depth = 10_000
              let deepXml = Text.StringBuilder(depth * 24)

              for _ in 1..depth do
                  deepXml.Append("<testsuite>") |> ignore

              deepXml.Append("<testcase classname=\"deep.Case\" name=\"deep-pass\"/>") |> ignore

              for _ in 1..depth do
                  deepXml.Append("</testsuite>") |> ignore

              File.WriteAllText(Path.Combine(baseRequest.Workspace, "deep.xml"), deepXml.ToString())
              let deeplyNested = junit "deep.xml"
              Expect.equal deeplyNested.Status Success "deep suite nesting does not consume the native call stack"
              Expect.equal deeplyNested.TestTotals (Some(1, 0, 0)) "the deepest direct testcase is counted once"
              Expect.isNone deeplyNested.StageWarning "the deeply nested case passed"
          }

          test "junit matches exact element local names across XML namespaces" {
              let root = tempRoot ()
              let baseRequest = request root ""

              let junit pattern allowEmpty =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          JUnitAllowEmptyResults = allowEmpty
                          Named = [ "testResults", pattern ] }

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "namespace-default.xml"),
                  "<testsuite xmlns=\"urn:fg215:default\" name=\"default\"><testcase name=\"pass\"/></testsuite>")
              let defaultNamespace = junit "namespace-default.xml" false
              Expect.equal defaultNamespace.Status Success "a default namespace does not hide exact local names"
              Expect.equal defaultNamespace.TestTotals (Some(1, 0, 0)) "the default-namespaced testcase is counted"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "namespace-prefixed.xml"),
                  "<j:testsuite xmlns:j=\"urn:fg215:prefixed\" name=\"prefixed\"><j:testcase name=\"failure\"><j:failure/></j:testcase></j:testsuite>")
              let prefixed = junit "namespace-prefixed.xml" false
              Expect.equal prefixed.Status Unstable "a prefixed failure retains its build contribution"
              Expect.equal prefixed.TestTotals (Some(1, 1, 0)) "prefixed suite, case, and marker names match by local name"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "namespace-mixed.xml"),
                  "<w:results xmlns:w=\"urn:fg215:wrapper\" xmlns:a=\"urn:fg215:a\" xmlns:b=\"urn:fg215:b\">"
                  + "<testsuite name=\"plain-suite\"><a:testcase name=\"pass\"/></testsuite>"
                  + "<b:testsuite name=\"prefixed-suite\"><testcase name=\"error\"><b:error/></testcase>"
                  + "<b:testcase name=\"skip\"><a:skipped/></b:testcase></b:testsuite></w:results>")
              let mixed = junit "namespace-mixed.xml" false
              Expect.equal mixed.Status Unstable "mixed namespaces preserve the failing aggregate"
              Expect.equal mixed.TestTotals (Some(3, 1, 1)) "wrapper, suite, case, and marker prefixes vary independently"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "namespace-uppercase.xml"),
                  "<testsuites><TESTSUITE name=\"upper\"><TESTCASE name=\"ignored\"/></TESTSUITE></testsuites>")
              let uppercase = junit "namespace-uppercase.xml" true
              Expect.equal uppercase.Status Success "allowEmpty permits exact-case misses"
              Expect.equal uppercase.TestTotals (Some(0, 0, 0)) "local-name matching remains case-sensitive"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "namespace-nonexact.xml"),
                  "<testsuites><testsuite-extra name=\"extra\"><testcase-extra name=\"ignored\"/></testsuite-extra></testsuites>")
              let nonexact = junit "namespace-nonexact.xml" true
              Expect.equal nonexact.Status Success "allowEmpty permits longer lookalikes"
              Expect.equal nonexact.TestTotals (Some(0, 0, 0)) "local-name matching remains text-exact"
          }

          test "junit matches ordered attribute local names but excludes namespace declarations" {
              let root = tempRoot ()
              let baseRequest = request root ""

              let junit pattern =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          JUnitAllowEmptyResults = false
                          Named = [ "testResults", pattern ] }

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "attribute-prefixed.xml"),
                  "<a:testsuite xmlns:a=\"urn:fg215:a\" xmlns:b=\"urn:fg215:b\" a:name=\"suite\" a:time=\"4\">"
                  + "<b:testcase b:name=\"plain\" b:classname=\"matrix.Case\" b:time=\"99\"/></a:testsuite>")
              let prefixed = junit "attribute-prefixed.xml"
              Expect.equal prefixed.Status Success "prefixed identity attributes admit the case"
              Expect.equal prefixed.TestTotals (Some(1, 0, 0)) "prefixed name and classname are matched by local name"
              Expect.equal prefixed.TestDuration (Some 4.0f) "the prefixed suite time is authoritative"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "attribute-first-a.xml"),
                  "<testsuite xmlns:a=\"urn:fg215:a\" xmlns:b=\"urn:fg215:b\" name=\"suite\" a:time=\"1.25\" b:time=\"9\">"
                  + "<testcase name=\"pass\"/></testsuite>")
              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "attribute-first-b.xml"),
                  "<testsuite xmlns:a=\"urn:fg215:a\" xmlns:b=\"urn:fg215:b\" name=\"suite\" b:time=\"9\" a:time=\"1.25\">"
                  + "<testcase name=\"pass\"/></testsuite>")
              Expect.equal (junit "attribute-first-a.xml").TestDuration (Some 1.25f) "the first local time wins in one order"
              Expect.equal (junit "attribute-first-b.xml").TestDuration (Some 9.0f) "the first local time wins in reverse order"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "attribute-xmlns-decoy.xml"),
                  "<n:testsuite xmlns:n=\"urn:fg215:n\" xmlns:name=\"urn:fg215:decoy\">"
                  + "<n:testcase n:name=\"plain\"/></n:testsuite>")
              let declarationDecoy = junit "attribute-xmlns-decoy.xml"
              Expect.equal declarationDecoy.Status Failure "a namespace declaration cannot supply the owner name"
              Expect.equal
                  declarationDecoy.Diagnostic
                  (Some "Cannot invoke \"String.lastIndexOf(int)\" because \"this.className\" is null")
                  "the xmlns:name decoy stays on the missing-identity path"
              Expect.isNone declarationDecoy.TestTotals "the terminal identity failure publishes no partial counts"
          }

          test "junit report patterns are case-sensitive without changing shared archive matching" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let reports = Path.Combine(baseRequest.Workspace, "reports")
              Directory.CreateDirectory reports |> ignore

              let junit pattern allowEmpty =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          JUnitAllowEmptyResults = allowEmpty
                          Named = [ "testResults", pattern ] }

              File.WriteAllText(
                  Path.Combine(reports, "Result.XML"),
                  "<testsuite name=\"exact\" time=\"1.25\"><testcase name=\"pass\"/></testsuite>")

              let exact = junit "reports/Result.XML" false
              Expect.equal exact.Status Success "an exact-case report path is selected"
              Expect.equal exact.TestTotals (Some(1, 0, 0)) "the exact-case report contributes one pass"
              Expect.equal exact.TestDuration (Some 1.25f) "the exact-case report contributes its duration"

              let missed = junit "reports/result.xml" false
              Expect.equal missed.Status Failure "a case-only mismatch is a no-match by default"
              Expect.equal
                  missed.Diagnostic
                  (Some "No test report files were found. Configuration error?")
                  "the existing no-report diagnostic owns the case-only miss"
              Expect.isNone missed.TestTotals "a terminal case-only miss publishes no counts"

              let allowedMiss = junit "reports/result.xml" true
              Expect.equal allowedMiss.Status Success "allowEmptyResults permits the case-only miss"
              Expect.equal allowedMiss.TestTotals (Some(0, 0, 0)) "the permitted miss returns the zero summary"
              Expect.equal allowedMiss.TestDuration (Some 0.0f) "the permitted miss returns zero duration"

              File.WriteAllText(
                  Path.Combine(reports, "result-pass.xml"),
                  "<testsuite name=\"lower\"><testcase name=\"pass\"/></testsuite>")
              File.WriteAllText(
                  Path.Combine(reports, "RESULT-fail.xml"),
                  "<testsuite name=\"upper\"><testcase name=\"fail\"><failure/></testcase></testsuite>")

              let wildcard = junit "reports/result-*.xml" false
              Expect.equal wildcard.Status Success "literal segments beside a wildcard retain exact case"
              Expect.equal wildcard.TestTotals (Some(1, 0, 0)) "the differently cased failing report stays inert"

              Expect.equal
                  (Publish.expandGlob baseRequest.Workspace "reports/RESULT-pass.xml")
                  [ "reports/result-pass.xml" ]
                  "the shared archive/stash glob entry point retains its existing case-insensitive behavior"
          }

          test "junit applies all pinned Ant default excludes without changing shared matching" {
              let root = tempRoot ()
              let baseRequest = request root ""

              let writeReport (relative: string) (xml: string) =
                  let path = Path.Combine(baseRequest.Workspace, relative)
                  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                  File.WriteAllText(path, xml)

              let passing (name: string) (time: string) =
                  $"<testsuite name=\"{name}\"><testcase name=\"pass\" time=\"{time}\"/></testsuite>"

              let failing (name: string) =
                  $"<testsuite name=\"{name}\"><testcase name=\"fail\" time=\"2.5\"><failure/></testcase></testsuite>"

              let excluded =
                  [ "reports/temp/result.xml~"
                    "reports/temp/#result.xml#"
                    "reports/temp/.#result.xml"
                    "reports/temp/%result.xml%"
                    "reports/temp/._result.xml"
                    "reports/files/CVS"
                    "reports/directories/CVS/hidden.xml"
                    "reports/meta/.cvsignore"
                    "reports/files/SCCS"
                    "reports/directories/SCCS/hidden.xml"
                    "reports/meta/vssver.scc"
                    "reports/files/.svn"
                    "reports/directories/.svn/hidden.xml"
                    "reports/files/.git"
                    "reports/directories/.git/hidden.xml"
                    "reports/meta/.gitattributes"
                    "reports/meta/.gitignore"
                    "reports/meta/.gitmodules"
                    "reports/files/.hg"
                    "reports/directories/.hg/hidden.xml"
                    "reports/meta/.hgignore"
                    "reports/meta/.hgsub"
                    "reports/meta/.hgsubstate"
                    "reports/meta/.hgtags"
                    "reports/files/.bzr"
                    "reports/directories/.bzr/hidden.xml"
                    "reports/meta/.bzrignore"
                    "reports/meta/.DS_Store" ]

              excluded
              |> List.iteri (fun index relative -> writeReport relative (failing $"excluded-{index}"))

              writeReport "reports/visible.xml" (passing "visible" "1.25")

              let controls =
                  [ "reports/.SVN/control.xml"
                    "reports/.gitx/control.xml"
                    "reports/CVSx/control.xml"
                    "reports/result.xml.bak" ]

              controls
              |> List.iteri (fun index relative -> writeReport relative (passing $"control-{index}" "0.25"))

              let junit pattern allowEmpty =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          JUnitAllowEmptyResults = allowEmpty
                          Named = [ "testResults", pattern ] }

              let broad = junit "**/*" false
              Expect.equal broad.Status Success "all 28 excluded witnesses remain inert"
              Expect.equal broad.TestTotals (Some(5, 0, 0)) "only the visible report and four near-miss controls contribute"
              Expect.equal broad.TestDuration (Some 2.25f) "excluded report durations do not leak into the summary"

              let explicit = "reports/directories/.svn/hidden.xml"
              let explicitMiss = junit explicit false
              Expect.equal explicitMiss.Status Failure "a literal include cannot override Ant default excludes"
              Expect.equal
                  explicitMiss.Diagnostic
                  (Some "No test report files were found. Configuration error?")
                  "the excluded-only selection follows the existing no-report path"
              Expect.isNone explicitMiss.TestTotals "the terminal excluded-only invocation publishes no counts"

              let allowedMiss = junit explicit true
              Expect.equal allowedMiss.Status Success "allowEmptyResults permits an excluded-only selection"
              Expect.equal allowedMiss.TestTotals (Some(0, 0, 0)) "the permitted excluded-only selection returns zero counts"
              Expect.equal allowedMiss.TestDuration (Some 0.0f) "the permitted excluded-only selection returns zero duration"

              Expect.equal
                  (Publish.expandGlob baseRequest.Workspace explicit)
                  [ explicit ]
                  "the shared archive/stash matcher still returns a default-excluded path"
          }

          test "junit collapses repeated report-pattern separators without changing shared matching" {
              let root = tempRoot ()
              let baseRequest = request root ""

              let writeReport (relative: string) (xml: string) =
                  let path = Path.Combine(baseRequest.Workspace, relative)
                  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                  File.WriteAllText(path, xml)

              let passing name time =
                  $"<testsuite name=\"{name}\"><testcase name=\"pass\" time=\"{time}\"/></testsuite>"

              let failing name =
                  $"<testsuite name=\"{name}\"><testcase name=\"fail\"><failure/></testcase></testsuite>"

              writeReport "reports/result.xml" (passing "literal" "1.25")
              writeReport "module/service-target/surefire-reports/TEST-result.xml" (passing "corpus" "2.5")
              writeReport "module/service-target/surefire-reports/test-decoy.xml" (failing "case-decoy")
              writeReport "module/.svn/service-target/surefire-reports/TEST-hidden.xml" (failing "excluded")

              let junit pattern allowEmpty =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          JUnitAllowEmptyResults = allowEmpty
                          Named = [ "testResults", pattern ] }

              let literal = junit "reports//result.xml" false
              Expect.equal literal.Status Success "a doubled internal separator selects the literal report"
              Expect.equal literal.TestTotals (Some(1, 0, 0)) "the literal report contributes one pass"
              Expect.equal literal.TestDuration (Some 1.25f) "the literal report duration is preserved"

              let many = junit "reports////result.xml" false
              Expect.equal many.TestTotals literal.TestTotals "three or more adjacent separators collapse identically"
              Expect.equal many.TestDuration literal.TestDuration "separator-run width does not change duration"

              let corpusPattern = "**//*target/surefire-reports/TEST-*.xml"
              let corpus = junit corpusPattern false
              Expect.equal corpus.Status Success "the exact admitted-corpus spelling selects the nested report"
              Expect.equal corpus.TestTotals (Some(1, 0, 0)) "case and default-exclude controls stay inert"
              Expect.equal corpus.TestDuration (Some 2.5f) "only the selected visible report contributes duration"

              let widerRuns = junit "**////*target//surefire-reports///TEST-*.xml" false
              Expect.equal widerRuns.TestTotals corpus.TestTotals "multiple internal runs tokenize like single separators"
              Expect.equal widerRuns.TestDuration corpus.TestDuration "multiple internal runs preserve the same summary"

              let caseMiss = junit "reports//Result.xml" false
              Expect.equal caseMiss.Status Failure "separator normalization does not weaken case sensitivity"
              Expect.equal
                  caseMiss.Diagnostic
                  (Some "No test report files were found. Configuration error?")
                  "a case-only miss retains the existing no-report diagnostic"

              let allowedCaseMiss = junit "reports//Result.xml" true
              Expect.equal allowedCaseMiss.Status Success "allowEmptyResults still permits the normalized case miss"
              Expect.equal allowedCaseMiss.TestTotals (Some(0, 0, 0)) "the permitted miss returns zero counts"

              let rooted = junit "//reports/result.xml" true
              Expect.equal rooted.TestTotals (Some(0, 0, 0)) "collapsing separators does not relativize a rooted include"

              Expect.equal
                  (Publish.expandGlob baseRequest.Workspace "reports//result.xml")
                  []
                  "the shared archive/stash matcher retains its existing repeated-separator behavior"
          }

          test "junit expands trailing report-pattern separators as recursive directory shorthand" {
              let root = tempRoot ()
              let baseRequest = request root ""

              let writeReport (relative: string) (xml: string) =
                  let path = Path.Combine(baseRequest.Workspace, relative)
                  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                  File.WriteAllText(path, xml)

              let passing name time =
                  $"<testsuite name=\"{name}\" time=\"{time}\"><testcase name=\"pass\"/></testsuite>"

              let failing name time =
                  $"<testsuite name=\"{name}\" time=\"{time}\"><testcase name=\"fail\"><failure/></testcase></testsuite>"

              writeReport "reports/top.xml" (passing "top" "1.25")
              writeReport "reports/deep/result.xml" (failing "deep" "2.5")
              writeReport "reports/.git/hidden.xml" (failing "excluded" "9")
              writeReport "outside/result.xml" (failing "outside" "11")

              let junit pattern allowEmpty =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          JUnitAllowEmptyResults = allowEmpty
                          Named = [ "testResults", pattern ] }

              let assertRecursive label observed =
                  Expect.equal observed.Status Unstable $"{label} selects the failing nested report"
                  Expect.equal observed.TestTotals (Some(2, 1, 0)) $"{label} selects direct and nested reports only"
                  Expect.equal observed.TestDuration (Some 3.75f) $"{label} excludes hidden and outside durations"

              assertRecursive "a singular trailing separator" (junit "reports/" false)
              assertRecursive "repeated trailing separators" (junit "reports////" false)
              assertRecursive "a trailing backslash separator" (junit "reports\\" false)
              assertRecursive "the existing comma-token trim" (junit "reports/ " false)

              let wildcardDirectory = junit "reports/*/" false
              assertRecursive "terminal double-star's zero-component arm" wildcardDirectory

              let exact = junit "reports/top.xml" false
              Expect.equal exact.Status Success "an exact non-trailing include retains its existing behavior"
              Expect.equal exact.TestTotals (Some(1, 0, 0)) "the exact include selects only its report"

              let literalFileWithSeparator = junit "reports/top.xml/" false
              Expect.equal
                  literalFileWithSeparator.Status
                  Failure
                  "a literal file path does not acquire the wildcard zero-component arm"
              Expect.equal
                  literalFileWithSeparator.Diagnostic
                  (Some "No test report files were found. Configuration error?")
                  "a trailing separator keeps a wholly literal prefix as a directory lookup"

              let caseMiss = junit "Reports/" false
              Expect.equal caseMiss.Status Failure "directory shorthand remains case-sensitive"
              Expect.equal
                  caseMiss.Diagnostic
                  (Some "No test report files were found. Configuration error?")
                  "a shorthand case miss retains the no-report diagnostic"

              let allowedMiss = junit "Reports/" true
              Expect.equal allowedMiss.Status Success "allowEmptyResults permits the shorthand case miss"
              Expect.equal allowedMiss.TestTotals (Some(0, 0, 0)) "the permitted case miss returns zero counts"

              for boundaryPattern in [ "/"; "//"; ""; "./reports/" ] do
                  let observed = junit boundaryPattern true
                  Expect.equal
                      observed.TestTotals
                      (Some(0, 0, 0))
                      $"boundary control '{boundaryPattern}' does not become scanner-relative shorthand"

              Expect.equal
                  (Publish.expandGlob baseRequest.Workspace "reports/")
                  []
                  "the shared archive/stash matcher retains its trailing-separator behavior"
              Expect.equal
                  (Publish.expandGlob baseRequest.Workspace "reports//")
                  []
                  "the shared matcher also retains its repeated trailing-separator behavior"
          }

          test "junit follows healthy symlinks and bounds repeated directory targets like pinned Ant" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let reports = Path.Combine(baseRequest.Workspace, "reports")
              Directory.CreateDirectory reports |> ignore

              let report name duration =
                  $"<testsuite name=\"{name}\" time=\"{duration}\" tests=\"1\"><testcase name=\"pass\"/></testsuite>"

              let direct = Path.Combine(reports, "direct.xml")
              File.WriteAllText(direct, report "direct" 1)
              File.CreateSymbolicLink(Path.Combine(reports, "file-link.xml"), direct) |> ignore

              let nested = Path.Combine(reports, "nested")
              Directory.CreateDirectory nested |> ignore
              File.WriteAllText(Path.Combine(nested, "nested.xml"), report "nested" 2)
              Directory.CreateSymbolicLink(Path.Combine(reports, "dir-link"), nested) |> ignore

              let external = tempRoot ()
              File.WriteAllText(Path.Combine(external, "external.xml"), report "external" 3)
              Directory.CreateSymbolicLink(Path.Combine(reports, "external-link"), external) |> ignore

              File.CreateSymbolicLink(
                  Path.Combine(reports, "broken.xml"),
                  Path.Combine(reports, "missing.xml"))
              |> ignore

              Directory.CreateSymbolicLink(
                  Path.Combine(reports, "broken-dir"),
                  Path.Combine(reports, "missing-dir"))
              |> ignore

              File.CreateSymbolicLink(
                  Path.Combine(reports, "self-loop.xml"),
                  Path.Combine(reports, "self-loop.xml"))
              |> ignore

              let junit pattern allowEmpty =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          Named = [ "testResults", pattern ]
                          JUnitAllowEmptyResults = allowEmpty }

              for label, pattern, duration in
                  [ "file", "reports/file-link.xml", 1.0f
                    "directory", "reports/dir-link/*.xml", 2.0f
                    "external", "reports/external-link/*.xml", 3.0f ] do
                  let observed = junit pattern false
                  Expect.equal observed.Status Success $"{label}: a healthy link is followed"
                  Expect.equal observed.TestTotals (Some(1, 0, 0)) $"{label}: one logical report is selected"
                  Expect.equal observed.TestDuration (Some duration) $"{label}: target content is parsed"

              let brokenLiteral = junit "reports/broken.xml" false
              Expect.equal brokenLiteral.Status Failure "a dangling literal link is absent from Ant's fast path"
              Expect.equal
                  brokenLiteral.Diagnostic
                  (Some "No test report files were found. Configuration error?")
                  "a dangling literal link takes the existing no-report path"

              let brokenLiteralAllowed = junit "reports/broken.xml" true
              Expect.equal brokenLiteralAllowed.Status Success "allowEmptyResults permits the literal dangling miss"
              Expect.equal brokenLiteralAllowed.TestTotals (Some(0, 0, 0)) "the permitted miss returns typed zero"

              let brokenWildcard = junit "reports/broken*.xml" false
              Expect.equal brokenWildcard.Status Unstable "a wildcard scan retains the dangling lexical file entry"
              Expect.equal brokenWildcard.TestTotals (Some(1, 1, 0)) "the dangling wildcard entry is one synthetic failure"
              Expect.equal brokenWildcard.TestDuration (Some 0.0f) "the synthetic report has zero duration"

              let brokenWildcardAllowed = junit "reports/broken*.xml" true
              Expect.equal
                  brokenWildcardAllowed.Status
                  Unstable
                  "allowEmptyResults does not suppress a selected dangling wildcard entry"
              Expect.equal
                  brokenWildcardAllowed.TestTotals
                  (Some(1, 1, 0))
                  "allowEmptyResults changes only the no-match path"

              let brokenDirectory = junit "reports/broken-dir/**/*.xml" true
              Expect.equal brokenDirectory.Status Success "a dangling directory link has no descendants"
              Expect.equal brokenDirectory.TestTotals (Some(0, 0, 0)) "the dangling directory link is a no-match"

              let selfFileLoop = junit "reports/self-*.xml" false
              Expect.equal selfFileLoop.Status Unstable "a file symlink loop is retained lexically"
              Expect.equal selfFileLoop.TestTotals (Some(1, 1, 0)) "the selected file loop is one synthetic failure"
              Expect.equal selfFileLoop.TestDuration (Some 0.0f) "the file loop synthetic report has zero duration"

              let brokenWithTimestampFilter =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          Named = [ "testResults", "reports/broken*.xml" ]
                          JUnitSkipOldReportsSince = Some(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) }

              Expect.equal
                  brokenWithTimestampFilter.Status
                  Failure
                  "skipOldReports inspects the final target before zero-length synthesis"
              Expect.stringContains
                  brokenWithTimestampFilter.Diagnostic.Value
                  "FileNotFoundException"
                  "a dangling target fails the timestamp lookup like Jenkins"

              let emptyTarget = Path.Combine(external, "empty.data")
              File.WriteAllBytes(emptyTarget, Array.empty)
              File.CreateSymbolicLink(Path.Combine(reports, "empty-link.data"), emptyTarget) |> ignore

              let emptyLink = junit "reports/empty-link.data" false
              Expect.equal emptyLink.Status Unstable "length is read from the final zero-byte target"
              Expect.equal emptyLink.TestTotals (Some(1, 1, 0)) "the zero-byte target is one synthetic failure"

              let buildStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
              let oldTarget = Path.Combine(external, "old.xml")
              File.WriteAllText(oldTarget, report "old" 4)
              File.SetLastWriteTimeUtc(
                  oldTarget,
                  DateTimeOffset.FromUnixTimeMilliseconds(buildStart - 4000L).UtcDateTime)
              File.CreateSymbolicLink(Path.Combine(reports, "old-link.xml"), oldTarget) |> ignore

              let oldLink =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          Named = [ "testResults", "reports/old-link.xml" ]
                          JUnitSkipOldReportsSince = Some buildStart
                          JUnitAllowEmptyResults = true }

              Expect.equal oldLink.Status Success "skipOldReports reads the final target timestamp"
              Expect.equal oldLink.TestTotals (Some(0, 0, 0)) "an old symlink target is filtered"

              let loops = Path.Combine(baseRequest.Workspace, "loops")
              Directory.CreateDirectory loops |> ignore
              File.WriteAllText(Path.Combine(loops, "looped.xml"), report "looped" 1)
              Directory.CreateSymbolicLink(Path.Combine(loops, "loop"), ".") |> ignore

              let boundedLoop = junit "loops/**/*.xml" false
              Expect.equal boundedLoop.Status Success "a self-loop terminates without poisoning publication"
              Expect.equal boundedLoop.TestTotals (Some(6, 0, 0)) "the base report plus five followed aliases are ingested"
              Expect.equal boundedLoop.TestDuration (Some 6.0f) "all six logical Ant paths retain their content"

              Expect.equal
                  (Publish.expandGlob baseRequest.Workspace "loops/**/*.xml" |> List.length)
                  41
                  "the public archive/stash matcher retains its platform traversal ceiling"
          }

          test "junit prunes unrelated external symlink directories before traversal" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let reports = Path.Combine(baseRequest.Workspace, "reports")
              Directory.CreateDirectory reports |> ignore
              File.WriteAllText(
                  Path.Combine(reports, "result.xml"),
                  "<testsuite name=\"good\" time=\"1\"><testcase name=\"pass\"/></testsuite>")

              let blocked = tempRoot ()
              let originalMode = File.GetUnixFileMode blocked
              Directory.CreateSymbolicLink(Path.Combine(baseRequest.Workspace, "unrelated"), blocked)
              |> ignore

              try
                  File.SetUnixFileMode(blocked, UnixFileMode.None)

                  let observed =
                      Executor.runStep
                          { baseRequest with
                              Name = "junit"
                              Script = None
                              Named = [ "testResults", "reports/*.xml" ] }

                  Expect.equal observed.Status Success "the narrow include succeeds"
                  Expect.equal observed.TestTotals (Some(1, 0, 0)) "only the selected report contributes"
                  Expect.equal observed.TestDuration (Some 1.0f) "the selected report duration is retained"
                  Expect.isNone observed.Diagnostic "the unrelated denied tree is never inspected"
              finally
                  File.SetUnixFileMode(blocked, originalMode)
          }

          test "junit polls cancellation while enumerating selected symlink directories" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let reports = Path.Combine(baseRequest.Workspace, "reports")
              Directory.CreateDirectory reports |> ignore
              let blocked = tempRoot ()
              let originalMode = File.GetUnixFileMode blocked
              Directory.CreateSymbolicLink(Path.Combine(reports, "external"), blocked) |> ignore
              let polls = ref 0

              let abort () =
                  polls.Value <- polls.Value + 1
                  polls.Value >= 2

              try
                  File.SetUnixFileMode(blocked, UnixFileMode.None)

                  let observed =
                      Publish.parseJUnitWithAbort
                          baseRequest.Workspace
                          [ "reports/**/*.xml" ]
                          None
                          abort

                  Expect.equal
                      observed
                      (Error JUnitProblem.Interrupted)
                      "enumeration returns the typed interruption"
                  Expect.equal polls.Value 2 "cancellation wins before the denied target is traversed"
              finally
                  File.SetUnixFileMode(blocked, originalMode)
          }

          test "junit fails closed when a selected symlink target is not accessible" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let reports = Path.Combine(baseRequest.Workspace, "reports")
              Directory.CreateDirectory reports |> ignore
              let denied = tempRoot ()
              let target = Path.Combine(denied, "target.xml")
              File.WriteAllText(
                  target,
                  "<testsuite name=\"denied\"><testcase name=\"pass\"/></testsuite>")
              File.CreateSymbolicLink(Path.Combine(reports, "permission-link.xml"), target) |> ignore
              let originalMode = File.GetUnixFileMode denied

              let junit pattern =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          Named = [ "testResults", pattern ]
                          JUnitAllowEmptyResults = true }

              try
                  File.SetUnixFileMode(denied, UnixFileMode.None)

                  for pattern in [ "reports/permission-link.xml"; "reports/permission-*.xml" ] do
                      let observed = junit pattern
                      Expect.equal observed.Status Failure $"{pattern}: authority failure is not allow-empty success"
                      Expect.isNone observed.TestTotals $"{pattern}: authority failure publishes no synthetic counts"
                      Expect.isNone observed.TestDuration $"{pattern}: authority failure publishes no duration"
                      Expect.stringContains
                          observed.Diagnostic.Value
                          "UnauthorizedAccessException"
                          $"{pattern}: the original authority failure class is retained"
              finally
                  File.SetUnixFileMode(denied, originalMode)
          }

          test "junit fails closed when a selected directory-link target is not accessible" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let reports = Path.Combine(baseRequest.Workspace, "reports")
              Directory.CreateDirectory reports |> ignore
              let deniedParent = tempRoot ()
              let targetDirectory = Path.Combine(deniedParent, "target-dir")
              Directory.CreateDirectory targetDirectory |> ignore
              File.WriteAllText(
                  Path.Combine(targetDirectory, "result.xml"),
                  "<testsuite name=\"denied\"><testcase name=\"pass\"/></testsuite>")
              Directory.CreateSymbolicLink(Path.Combine(reports, "external"), targetDirectory) |> ignore
              let originalMode = File.GetUnixFileMode deniedParent

              try
                  File.SetUnixFileMode(deniedParent, UnixFileMode.None)

                  let observed =
                      Executor.runStep
                          { baseRequest with
                              Name = "junit"
                              Script = None
                              Named = [ "testResults", "reports/external/**/*.xml" ]
                              JUnitAllowEmptyResults = true }

                  Expect.equal observed.Status Failure "directory-link authority failure is not allow-empty success"
                  Expect.isNone observed.TestTotals "directory-link authority failure publishes no counts"
                  Expect.isNone observed.TestDuration "directory-link authority failure publishes no duration"
                  Expect.stringContains
                      observed.Diagnostic.Value
                      "UnauthorizedAccessException"
                      "the directory-link authority failure class is retained"
              finally
                  File.SetUnixFileMode(deniedParent, originalMode)
          }

          test "junit stops branching symlink scans at the configured logical-entry budget" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let loops = Path.Combine(baseRequest.Workspace, "loops")
              Directory.CreateDirectory loops |> ignore
              File.WriteAllText(
                  Path.Combine(loops, "result.xml"),
                  "<testsuite name=\"loop\"><testcase name=\"pass\"/></testsuite>")
              Directory.CreateSymbolicLink(Path.Combine(loops, "a"), ".") |> ignore
              Directory.CreateSymbolicLink(Path.Combine(loops, "b"), ".") |> ignore

              let observed =
                  Publish.parseJUnitWithAbortUsingScanLimit
                      20
                      baseRequest.Workspace
                      [ "loops/**/*.xml" ]
                      None
                      (fun () -> false)

              Expect.equal
                  observed
                  (Error(Unreadable "JUnit report scan exceeded the 20 logical-entry safety limit"))
                  "branching aliases fail with a stable named safety limit"

              let patternBound =
                  Publish.parseJUnitWithAbortUsingScanLimit
                      20
                      baseRequest.Workspace
                      (List.init 30 (fun index -> $"missing-{index}.xml"))
                      None
                      (fun () -> false)

              Expect.equal
                  patternBound
                  (Error(Unreadable "JUnit report scan exceeded the 20 logical-entry safety limit"))
                  "pattern compilation and evaluation share the same stable work ceiling"
          }

          test "junit retains the selected physical report across directory-link retargeting" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let reports = Path.Combine(baseRequest.Workspace, "reports")
              Directory.CreateDirectory reports |> ignore
              let targetA = tempRoot ()
              let targetB = tempRoot ()
              File.WriteAllText(
                  Path.Combine(targetA, "result.xml"),
                  "<testsuite name=\"a\" time=\"1\"><testcase name=\"pass\"/></testsuite>")
              File.WriteAllText(
                  Path.Combine(targetB, "result.xml"),
                  "<testsuite name=\"b\" time=\"9\"><testcase name=\"pass\"/></testsuite>")

              let link = Path.Combine(reports, "external")
              Directory.CreateSymbolicLink(link, targetA) |> ignore
              let polls = ref 0

              let retargetAfterSelection () =
                  polls.Value <- polls.Value + 1

                  if polls.Value = 8 then
                      Directory.Delete link
                      Directory.CreateSymbolicLink(link, targetB) |> ignore

                  false

              let observed =
                  Publish.parseJUnitWithAbort
                      baseRequest.Workspace
                      [ "reports/external/*.xml" ]
                      None
                      retargetAfterSelection

              Expect.equal
                  observed
                  (Ok(1, 0, 0, Some 1.0f))
                  "the scanner opens target A by its retained physical path after the logical link points to B"
              Expect.isGreaterThanOrEqual polls.Value 8 "the deterministic retarget seam ran after selection"
          }

          test "junit skipOldReports filters before report construction at the pinned build-time boundary" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let buildStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

              let writeAt (relative: string) (content: string) (modifiedAt: int64) =
                  let path = Path.Combine(baseRequest.Workspace, relative)
                  Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                  File.WriteAllText(path, content)
                  File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(modifiedAt).UtcDateTime)

              writeAt
                  "reports/old-failure.xml"
                  "<testsuite name=\"old\" time=\"2\"><testcase name=\"bad\"><failure/></testcase></testsuite>"
                  (buildStart - 3001L)

              writeAt "reports/old-malformed.xml" "not xml" (buildStart - 4000L)

              writeAt
                  "reports/boundary.xml"
                  "<testsuite name=\"boundary\" time=\"1\"><testcase name=\"pass\"/></testsuite>"
                  (buildStart - 3000L)

              writeAt
                  "reports/future.xml"
                  "<testsuite name=\"future\" time=\"3\"><testcase name=\"skip\"><skipped/></testcase></testsuite>"
                  (buildStart + 1000L)

              let junit pattern since allowEmpty =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          Named = [ "testResults", pattern ]
                          JUnitSkipOldReportsSince = since
                          JUnitAllowEmptyResults = allowEmpty }

              let unfiltered = junit "reports/*.xml" None false
              Expect.equal unfiltered.Status Unstable "the omitted/false path performs no mtime filtering"
              Expect.equal unfiltered.TestTotals (Some(4, 2, 1)) "old valid and malformed reports are constructed"
              Expect.equal unfiltered.TestDuration (Some 6.0f) "all valid report durations contribute without filtering"

              let filtered = junit "reports/*.xml" (Some buildStart) false
              Expect.equal filtered.Status Success "old failing and malformed reports are silently skipped"
              Expect.equal filtered.TestTotals (Some(2, 0, 1)) "cutoff equality and future reports remain"
              Expect.equal filtered.TestDuration (Some 4.0f) "only retained report durations contribute"

              let allOld = junit "reports/old-*.xml" (Some buildStart) false
              Expect.equal allOld.Status Failure "matched-but-all-old is the zero-result terminal path"
              Expect.equal
                  allOld.Diagnostic
                  (Some "None of the test reports contained any result")
                  "all-old remains distinct from a no-match"

              let allowedAllOld = junit "reports/old-*.xml" (Some buildStart) true
              Expect.equal allowedAllOld.Status Success "allowEmptyResults permits the all-old aggregate"
              Expect.equal allowedAllOld.TestTotals (Some(0, 0, 0)) "the allowed all-old summary stays typed zero"
              Expect.equal allowedAllOld.TestDuration (Some 0.0f) "the allowed all-old duration stays Float zero"

              let vanishedPath = Path.Combine(baseRequest.Workspace, "reports/vanished.xml")
              writeAt
                  "reports/vanished.xml"
                  "<testsuite name=\"vanished\"><testcase name=\"pass\"/></testsuite>"
                  (buildStart + 1000L)

              let mutable polls = 0
              let deleteAtPoll =
                  (Directory.EnumerateFileSystemEntries(baseRequest.Workspace) |> Seq.length)
                  + (Directory.EnumerateFileSystemEntries(Path.GetDirectoryName vanishedPath) |> Seq.length)
                  + 4

              let vanishAfterScan () =
                  polls <- polls + 1

                  if polls = deleteAtPoll then
                      File.Delete vanishedPath

                  false

              match
                  Publish.parseJUnitWithAbort
                      baseRequest.Workspace
                      [ "reports/vanished.xml" ]
                      (Some buildStart)
                      vanishAfterScan
              with
              | Error(Unreadable message) ->
                  Expect.stringContains
                      message
                      "FileNotFoundException"
                      "skipOldReports fails a selected report that vanishes before metadata inspection"
              | other -> failtestf "vanished skipOldReports input returned %A" other
          }

          test "junit recognizes every reached owner and requires a resolvable testcase identity" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let emitted = ResizeArray<string>()
              let missingIdentity = "Cannot invoke \"String.lastIndexOf(int)\" because \"this.className\" is null"

              let junitWithOutput pattern allowEmpty =
                  emitted.Clear()

                  let observed =
                      Executor.runStep
                          { baseRequest with
                              Name = "junit"
                              Script = None
                              Named = [ "testResults", pattern ]
                              JUnitAllowEmptyResults = allowEmpty
                              OnLine = Some emitted.Add }

                  observed, List.ofSeq emitted

              let junit pattern allowEmpty = junitWithOutput pattern allowEmpty |> fst

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "root-classname.xml"),
                  "<arbitrary>"
                  + "<testcase classname=\"\" name=\"pass\"/>"
                  + "<testcase classname=\"matrix.Case\" name=\"failure\"><failure/></testcase>"
                  + "<testcase classname=\"matrix.Case\" name=\"error\"><error/></testcase>"
                  + "<testcase classname=\"matrix.Case\" name=\"skip\"><failure/><error/><skipped/></testcase>"
                  + "</arbitrary>")

              let classnamed = junit "root-classname.xml" false
              Expect.equal classnamed.Status Unstable "the document root owns its direct classnamed cases"
              Expect.equal classnamed.TestTotals (Some(4, 2, 1)) "empty classname is present and testcase markers retain skipped-first precedence"
              Expect.equal classnamed.StageWarning (Some Unstable) "root-owned failures decorate the stage"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "root-owner-name.xml"),
                  "<arbitrary name=\"\"><testcase name=\"owner-fallback\"/>"
                  + "<testsuite name=\"SuiteFallback\"><testcase name=\"suite-fallback\"/></testsuite>"
                  + "</arbitrary>")

              let ownerNamed = junit "root-owner-name.xml" false
              Expect.equal ownerNamed.Status Success "an explicitly empty owner name is still a present class fallback"
              Expect.equal ownerNamed.TestTotals (Some(2, 0, 0)) "root and reached-suite owner names each supply one pass"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "root-dotted-name.xml"),
                  "<arbitrary><testcase name=\"matrix.Root.dotted-fallback\"/>"
                  + "<testsuite><testcase name=\"matrix.Suite.dotted-fallback\"/></testsuite>"
                  + "</arbitrary>")

              let dotted = junit "root-dotted-name.xml" false
              Expect.equal dotted.Status Success "a dotted testcase name supplies the final class fallback"
              Expect.equal dotted.TestTotals (Some(2, 0, 0)) "root and reached-suite dotted names each supply one pass"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "root-error.xml"),
                  "<arbitrary><error/><skipped/></arbitrary>")

              let rootError = junit "root-error.xml" false
              Expect.equal rootError.Status Success "a reached root owns its direct synthetic error case"
              Expect.equal rootError.TestTotals (Some(1, 0, 1)) "the owner's direct skipped marker outranks its synthetic error"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "root-error-only.xml"),
                  "<arbitrary><error/></arbitrary>")

              let rootErrorOnly = junit "root-error-only.xml" false
              Expect.equal rootErrorOnly.Status Unstable "a root-direct error alone is one synthetic failure"
              Expect.equal rootErrorOnly.TestTotals (Some(1, 1, 0)) "the error-only root contributes exact failed counts"
              Expect.equal rootErrorOnly.StageWarning (Some Unstable) "the root synthetic failure decorates the stage"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "root-skipped-error.xml"),
                  "<arbitrary><skipped/><error/></arbitrary>")

              let rootSkippedError = junit "root-skipped-error.xml" false
              Expect.equal rootSkippedError.Status Success "root synthetic classification is XML-order independent"
              Expect.equal rootSkippedError.TestTotals (Some(1, 0, 1)) "skipped still outranks error in reverse XML order"
              Expect.isNone rootSkippedError.StageWarning "the reverse-order skipped synthetic case has no warning"

              let valid = "<arbitrary><testcase classname=\"matrix.Valid\" name=\"pass\"/></arbitrary>"
              let invalidRoot = "<arbitrary><testcase name=\"simple\"/></arbitrary>"
              let invalidSuite = "<testsuite><testcase name=\"simple\"/></testsuite>"
              let validSuite = "<testsuite name=\"valid\"><testcase name=\"pass\"/></testsuite>"

              File.WriteAllText(Path.Combine(baseRequest.Workspace, "identity-a-0-invalid.xml"), invalidRoot)
              File.WriteAllText(Path.Combine(baseRequest.Workspace, "identity-a-1-valid.xml"), valid)
              File.WriteAllText(Path.Combine(baseRequest.Workspace, "identity-b-0-valid.xml"), valid)
              File.WriteAllText(Path.Combine(baseRequest.Workspace, "identity-b-1-invalid.xml"), invalidSuite)

              for label, pattern, allowEmpty in
                  [ "invalid-first", "identity-a-*.xml", false
                    "invalid-last-allowed", "identity-b-*.xml", true ] do
                  let observed, output = junitWithOutput pattern allowEmpty
                  Expect.equal observed.Status Failure $"{label}: an unresolved class identity poisons the aggregate"
                  Expect.isNone observed.TestTotals $"{label}: terminal unreadability publishes no partial counts"
                  Expect.isNone observed.StageWarning $"{label}: unreadability is not a test-failure warning"
                  Expect.equal observed.Diagnostic (Some missingIdentity) $"{label}: the raw Jenkins reason remains durable"
                  Expect.equal
                      output
                      [ "Recording test results"; missingIdentity ]
                      $"{label}: the exact Jenkins null-className line follows the recording notice"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "same-xml-invalid-first.xml"),
                  "<testsuites>" + invalidSuite + validSuite + "</testsuites>")
              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "same-xml-invalid-last.xml"),
                  "<testsuites>" + validSuite + invalidSuite + "</testsuites>")

              for label, pattern, allowEmpty in
                  [ "same-xml-invalid-first", "same-xml-invalid-first.xml", false
                    "same-xml-invalid-last-allowed", "same-xml-invalid-last.xml", true ] do
                  let observed, output = junitWithOutput pattern allowEmpty
                  Expect.equal observed.Status Failure $"{label}: an invalid direct-suite sibling poisons its report"
                  Expect.isNone observed.TestTotals $"{label}: same-report poisoning publishes no partial counts"
                  Expect.isNone observed.StageWarning $"{label}: same-report identity failure is not a test warning"
                  Expect.equal observed.Diagnostic (Some missingIdentity) $"{label}: the same-report reason remains durable"
                  Expect.equal
                      output
                      [ "Recording test results"; missingIdentity ]
                      $"{label}: both XML orders emit the exact Jenkins null-className line"
          }

          test "junit distinguishes a missing testcase name from an empty one and preserves parser order" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let emitted = ResizeArray<string>()
              let missingTestName = JUnitDiagnostics.MissingTestNameMessage
              let missingIdentity = "Cannot invoke \"String.lastIndexOf(int)\" because \"this.className\" is null"

              let junit pattern allowEmpty =
                  emitted.Clear()

                  let observed =
                      Executor.runStep
                          { baseRequest with
                              Name = "junit"
                              Script = None
                              Named = [ "testResults", pattern ]
                              JUnitAllowEmptyResults = allowEmpty
                              OnLine = Some emitted.Add }

                  observed, List.ofSeq emitted

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "missing-name-classname.xml"),
                  "<arbitrary>"
                  + "<testcase classname=\"matrix.Pass\"/>"
                  + "<testcase classname=\"matrix.Fail\"><failure/></testcase>"
                  + "<testcase classname=\"matrix.Error\"><error/></testcase>"
                  + "<testcase classname=\"matrix.Skip\"><failure/><error/><skipped/></testcase>"
                  + "</arbitrary>")

              let classnamed, _ = junit "missing-name-classname.xml" false
              Expect.equal classnamed.Status Unstable "classname makes absent testcase names admissible"
              Expect.equal classnamed.TestTotals (Some(4, 2, 1)) "all missing-name marker classes contribute"
              Expect.equal classnamed.StageWarning (Some Unstable) "missing names do not suppress ordinary warnings"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "missing-name-owner.xml"),
                  "<arbitrary name=\"\"><testcase/>"
                  + "<testsuite name=\"SuiteFallback\"><testcase/></testsuite>"
                  + "</arbitrary>")

              let ownerNamed, _ = junit "missing-name-owner.xml" false
              Expect.equal ownerNamed.Status Success "owner raw-name presence makes absent testcase names admissible"
              Expect.equal ownerNamed.TestTotals (Some(2, 0, 0)) "both reached owners contribute one unnamed pass"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "missing-name-terminal.xml"),
                  "<arbitrary><testcase/></arbitrary>")

              let missing, missingOutput = junit "missing-name-terminal.xml" true
              let missingReportPath = Path.Combine(baseRequest.Workspace, "missing-name-terminal.xml")
              Expect.equal missing.Status Failure "a missing name without a class fallback is terminal"
              Expect.isNone missing.TestTotals "allowEmptyResults cannot turn a missing name into zero totals"
              Expect.isNone missing.StageWarning "the parser fault is not a test warning"
              Expect.equal missing.Diagnostic (Some missingTestName) "the exact oracle diagnostic is durable"
              Expect.equal
                  missingOutput
                  [ "Recording test results"
                    $"Failed to read {missingReportPath}" ]
                  "the report-specific wrapper is visible while the hosted boundary owns the root cause"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "empty-name-terminal.xml"),
                  "<arbitrary><testcase name=\"\"/></arbitrary>")

              let empty, emptyOutput = junit "empty-name-terminal.xml" true
              Expect.equal empty.Status Failure "an empty name remains FG-211's null-className path"
              Expect.equal empty.Diagnostic (Some missingIdentity) "missing and empty names remain distinct"
              Expect.equal emptyOutput [ "Recording test results"; missingIdentity ] "the FG-211 line remains exact"

              let assertFatal (label: string) (xml: string) (expected: string) =
                  let fileName = $"{label}.xml"
                  File.WriteAllText(Path.Combine(baseRequest.Workspace, fileName), xml)
                  let observed, output = junit fileName true
                  Expect.equal observed.Status Failure $"{label}: the selected parser fault is terminal"
                  Expect.equal observed.Diagnostic (Some expected) $"{label}: construction/tally precedence matches the parser"

                  let expectedOutput =
                      if expected = missingTestName then
                          [ "Recording test results"
                            $"Failed to read {Path.Combine(baseRequest.Workspace, fileName)}" ]
                      else
                          [ "Recording test results"; expected ]

                  Expect.equal output expectedOutput $"{label}: only the Jenkins-visible winning fault is emitted"

              assertFatal
                  "suite-document-order-missing-first"
                  "<testsuites><testsuite><testcase/></testsuite><testsuite><testcase name=\"simple\"/></testsuite></testsuites>"
                  missingTestName

              assertFatal
                  "suite-document-order-identity-first"
                  "<testsuites><testsuite><testcase name=\"simple\"/></testsuite><testsuite><testcase/></testsuite></testsuites>"
                  missingTestName

              assertFatal
                  "child-before-owner"
                  "<testsuites><testcase/><testsuite><testcase name=\"simple\"/></testsuite></testsuites>"
                  missingTestName

              assertFatal
                  "same-owner-missing-first"
                  "<testsuite><testcase/><testcase name=\"simple\"/></testsuite>"
                  missingTestName

              assertFatal
                  "same-owner-identity-first"
                  "<testsuite><testcase name=\"simple\"/><testcase/></testsuite>"
                  missingTestName

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "global-identity-missing-a-identity.xml"),
                  "<arbitrary><testcase name=\"simple\"/></arbitrary>")
              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "global-identity-missing-b-missing.xml"),
                  "<arbitrary><testcase/></arbitrary>")

              let identityThenMissing, identityThenMissingOutput =
                  junit "global-identity-missing-*" true

              let identityThenMissingReport =
                  Path.Combine(baseRequest.Workspace, "global-identity-missing-b-missing.xml")

              Expect.equal identityThenMissing.Status Failure "construction across all files precedes identity tally"
              Expect.isNone identityThenMissing.TestTotals "the deferred identity path publishes no speculative totals"
              Expect.equal identityThenMissing.Diagnostic (Some missingTestName) "a later construction fault wins"
              Expect.equal
                  identityThenMissingOutput
                  [ "Recording test results"; $"Failed to read {identityThenMissingReport}" ]
                  "the later missing-name report owns the visible wrapper"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "global-missing-unreadable-a-missing.xml"),
                  "<arbitrary><testcase/></arbitrary>")
              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "global-missing-unreadable-b-unreadable.XML"),
                  "not xml")

              let missingThenUnreadable, missingThenUnreadableOutput =
                  junit
                      "global-missing-unreadable-b-unreadable.XML,global-missing-unreadable-a-missing.xml"
                      true

              let missingThenUnreadableReport =
                  Path.Combine(baseRequest.Workspace, "global-missing-unreadable-a-missing.xml")

              Expect.equal missingThenUnreadable.Status Failure "the first globally sorted immediate fault wins"
              Expect.isNone missingThenUnreadable.TestTotals "an immediate fault publishes no speculative totals"
              Expect.equal missingThenUnreadable.Diagnostic (Some missingTestName) "the later unreadable file is not read"
              Expect.equal
                  missingThenUnreadableOutput
                  [ "Recording test results"; $"Failed to read {missingThenUnreadableReport}" ]
                  "global file sorting, not comma-pattern order, selects the missing-name wrapper"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "global-unreadable-missing-a-unreadable.XML"),
                  "not xml")
              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "global-unreadable-missing-b-missing.xml"),
                  "<arbitrary><testcase/></arbitrary>")

              let unreadableThenMissing, unreadableThenMissingOutput =
                  junit
                      "global-unreadable-missing-b-missing.xml,global-unreadable-missing-a-unreadable.XML"
                      true

              Expect.equal unreadableThenMissing.Status Failure "an earlier globally sorted unreadable report wins"
              Expect.isNone unreadableThenMissing.TestTotals "the unreadable path publishes no speculative totals"
              Expect.isNone unreadableThenMissing.StageWarning "generic unreadability remains outside test-warning classification"
              Expect.equal unreadableThenMissingOutput [ "Recording test results" ] "the unreadable report stops construction"
              Expect.stringContains
                  (defaultArg unreadableThenMissing.Diagnostic "")
                  "global-unreadable-missing-a-unreadable.XML: XmlException"
                  "the first immediate read failure remains exact"
          }

          test "junit fails one aggregate with no recognized result unless typed allowEmptyResults permits it" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let emitted = ResizeArray<string>()

              let junit pattern allowEmpty =
                  emitted.Clear()

                  let observed =
                      Executor.runStep
                          { baseRequest with
                              Name = "junit"
                              Script = None
                              Named = [ "testResults", pattern ]
                              JUnitAllowEmptyResults = allowEmpty
                              OnLine = Some emitted.Add }

                  observed, List.ofSeq emitted

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "zero-attributes.xml"),
                  "<testsuite tests=\"999\" failures=\"999\" errors=\"999\" skipped=\"999\"/>")
              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "zero-testsuites.xml"),
                  "<testsuites/>")
              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "zero-skipped-suite.xml"),
                  "<testsuite><skipped/></testsuite>")

              let empty, emptyOutput = junit "zero-*.xml" false
              Expect.equal empty.Status Failure "all zero-result siblings produce one terminal aggregate failure"
              Expect.isNone empty.TestTotals "a terminal empty aggregate publishes no summary"
              Expect.isNone empty.StageWarning "empty input is not a test-failure WarningAction"
              Expect.equal
                  empty.Diagnostic
                  (Some "None of the test reports contained any result")
                  "the aggregate-empty reason is distinct from a missing glob"
              Expect.equal
                  emptyOutput
                  [ "Recording test results"; "None of the test reports contained any result" ]
                  "the terminal reason is emitted before it remains available as a diagnostic"

              let suppressed =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          Named = [ "testResults", "zero-*.xml" ]
                          JUnitSkipMarkingBuildUnstable = true
                          JUnitSkipMarkingStageUnstable = true }

              Expect.equal suppressed.Status Failure "instability flags cannot suppress a terminal empty aggregate"
              Expect.isNone suppressed.TestTotals "suppression cannot manufacture an empty summary"
              Expect.isNone suppressed.StageWarning "the terminal failure never decorates the stage"

              let allowed, allowedOutput = junit "zero-*.xml" true
              Expect.equal allowed.Status Success "allowEmptyResults makes the empty aggregate nonterminal"
              Expect.equal allowed.TestTotals (Some(0, 0, 0)) "the permitted call returns an exact zero summary"
              Expect.equal allowed.TestDuration (Some 0.0f) "the permitted empty aggregate returns zero Float duration"
              Expect.isNone allowed.StageWarning "a permitted empty result has no stage warning"
              Expect.equal
                  allowedOutput
                  [ "Recording test results"; "None of the test reports contained any result" ]
                  "the permitted empty aggregate remains visible in the log"

              let missing, missingOutput = junit "nothing-matches-*.xml" false
              Expect.equal missing.Status Failure "a missing report remains terminal by default"
              Expect.isNone missing.TestTotals "a missing report publishes no summary by default"
              Expect.equal
                  missing.Diagnostic
                  (Some "No test report files were found. Configuration error?")
                  "missing reports keep their distinct pinned reason"
              Expect.equal
                  missingOutput
                  [ "Recording test results"; "No test report files were found. Configuration error?" ]
                  "the terminal no-report notice is emitted exactly"

              let allowedMissing, allowedMissingOutput = junit "nothing-matches-*.xml" true
              Expect.equal allowedMissing.Status Success "allowEmptyResults also permits a missing glob"
              Expect.equal allowedMissing.TestTotals (Some(0, 0, 0)) "the permitted missing glob returns the zero summary"
              Expect.equal allowedMissing.TestDuration (Some 0.0f) "the permitted missing glob returns zero Float duration"
              Expect.equal
                  allowedMissingOutput
                  [ "Recording test results"
                    "No test report files were found. Configuration error?"
                    "None of the test reports contained any result" ]
                  "the parser and aggregate-empty notices are both emitted"

              let polls = ref 0

              let abortAfterScan () =
                  polls.Value <- polls.Value + 1
                  polls.Value = 3

              let interrupted =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          Named = [ "testResults", "zero-attributes.xml" ]
                          JUnitAllowEmptyResults = true
                          DeadlineExpired = Some abortAfterScan }

              Expect.equal polls.Value 3 "the interrupt fired only at the final aggregate poll"
              Expect.equal interrupted.Status Aborted "post-scan interruption outranks allowed empty results"
              Expect.isNone interrupted.TestTotals "an interrupted empty scan publishes no summary"
              Expect.isNone interrupted.StageWarning "an interrupted scan never decorates the stage"
          }

          test "junit synthesizes failed tests for malformed XML while real unreadability and interruption remain terminal" {
              let root = tempRoot ()
              let baseRequest = request root ""
              let report = Path.Combine(baseRequest.Workspace, "report.xml")

              let junit pattern skipBuild skipStage deadlineExpired =
                  Executor.runStep
                      { baseRequest with
                          Name = "junit"
                          Script = None
                          Named = [ "testResults", pattern ]
                          JUnitSkipMarkingBuildUnstable = skipBuild
                          JUnitSkipMarkingStageUnstable = skipStage
                          DeadlineExpired = deadlineExpired }

              File.WriteAllText(report, "not xml")
              let malformed = junit "report.xml" false false None
              Expect.equal malformed.Status Unstable "a malformed .xml report is one synthetic failed test"
              Expect.equal malformed.TestTotals (Some(1, 1, 0)) "the synthetic case contributes exact counts"
              Expect.equal malformed.StageWarning (Some Unstable) "the synthetic failure decorates the stage"

              let suppressed = junit "report.xml" true true None
              Expect.equal suppressed.Status Success "ordinary JUnit marking flags suppress the synthetic failure's result channels"
              Expect.equal suppressed.TestTotals (Some(1, 1, 0)) "suppression never erases the synthetic counts"
              Expect.isNone suppressed.StageWarning "stage suppression removes only the warning"

              File.WriteAllText(report, "")
              let empty = junit "report.xml" false false None
              Expect.equal empty.Status Unstable "an empty report is the plugin's sibling synthetic failed case"
              Expect.equal empty.TestTotals (Some(1, 1, 0)) "the empty-report case contributes the same summary counts"
              Expect.equal empty.StageWarning (Some Unstable) "the empty-report failure decorates the stage"

              let emptyText = Path.Combine(baseRequest.Workspace, "empty.txt")
              File.WriteAllText(emptyText, "")
              let emptyTextResult = junit "empty.txt" false false None
              Expect.equal emptyTextResult.Status Unstable "zero-byte detection precedes the malformed-report extension gate"
              Expect.equal emptyTextResult.TestTotals (Some(1, 1, 0)) "an empty non-XML report contributes the synthetic case"
              Expect.equal emptyTextResult.StageWarning (Some Unstable) "an empty non-XML report is an ordinary test warning"

              let emptyUppercase = Path.Combine(baseRequest.Workspace, "empty.XML")
              File.WriteAllText(emptyUppercase, "")
              let emptyUppercaseResult = junit "empty.XML" false false None
              Expect.equal emptyUppercaseResult.Status Unstable "empty uppercase .XML is recovered before case-sensitive parsing"
              Expect.equal emptyUppercaseResult.TestTotals (Some(1, 1, 0)) "empty uppercase .XML contributes the synthetic case"
              Expect.equal emptyUppercaseResult.StageWarning (Some Unstable) "empty uppercase .XML is an ordinary test warning"

              File.Delete emptyText
              File.Delete emptyUppercase

              File.WriteAllText(report, "not xml")

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "valid.xml"),
                  "<testsuite name=\"valid\" tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"ok\"/></testsuite>")

              let mixed = junit "*.xml" false false None
              Expect.equal mixed.Status Unstable "a malformed file does not discard valid sibling reports"
              Expect.equal mixed.TestTotals (Some(2, 1, 0)) "valid totals and one synthetic failure aggregate"
              Expect.equal mixed.StageWarning (Some Unstable) "the mixed aggregate remains a test-failure warning"

              File.WriteAllText(Path.Combine(baseRequest.Workspace, "multi-a.xml"), "not xml")
              File.WriteAllText(Path.Combine(baseRequest.Workspace, "multi-b.xml"), "<testsuite>")
              let multiple = junit "multi-*.xml" false false None
              Expect.equal multiple.Status Unstable "each malformed XML file contributes independently"
              Expect.equal multiple.TestTotals (Some(2, 2, 0)) "synthesis is once per file, not once per invocation"
              Expect.equal multiple.StageWarning (Some Unstable) "multiple synthetic failures remain an ordinary warning"

              File.WriteAllText(
                  Path.Combine(baseRequest.Workspace, "attributes-only.xml"),
                  "<testsuite tests=\"2147483647\" failures=\"2147483647\" errors=\"0\" skipped=\"0\"/>")
              File.WriteAllText(Path.Combine(baseRequest.Workspace, "attributes-malformed.xml"), "not xml")
              let overflow = junit "attributes-*.xml" false false None
              Expect.equal overflow.Status Unstable "suite aggregate attributes cannot manufacture overflow"
              Expect.equal overflow.TestTotals (Some(1, 1, 0)) "only the malformed sibling's synthetic case contributes"
              Expect.equal overflow.StageWarning (Some Unstable) "the real synthetic failure remains an ordinary warning"

              let nonXml = Path.Combine(baseRequest.Workspace, "report.txt")
              File.WriteAllText(nonXml, "not xml")
              let unreadable = junit "report.txt" true true None
              Expect.equal unreadable.Status Failure "the plugin's synthetic rule is restricted to .xml paths"
              Expect.isNone unreadable.TestTotals "a genuinely unreadable input publishes no counts"
              Expect.isNone unreadable.StageWarning "unreadability is not a test-failure warning"

              File.WriteAllText(Path.Combine(baseRequest.Workspace, "report.XML"), "not xml")
              let uppercase = junit "report.XML" true true None
              Expect.equal uppercase.Status Failure "the non-empty malformed-report extension gate is case-sensitive"
              Expect.isNone uppercase.TestTotals "non-empty uppercase .XML does not acquire synthetic counts"
              Expect.isNone uppercase.StageWarning "non-empty uppercase .XML failure is not a test warning"

              let ioReport = Path.Combine(baseRequest.Workspace, "io.xml")
              File.WriteAllText(ioReport, "<testsuite tests=\"1\" failures=\"0\" skipped=\"0\"/>")
              let vanishedPolls = ref 0
              let deleteAtPoll =
                  Directory.EnumerateFileSystemEntries(baseRequest.Workspace)
                  |> Seq.length
                  |> fun scannedEntries -> scannedEntries + 3

              let deleteAfterGlob () =
                  vanishedPolls.Value <- vanishedPolls.Value + 1
                  if vanishedPolls.Value = deleteAtPoll then File.Delete ioReport
                  false

              let vanished = junit "io.xml" false false (Some deleteAfterGlob)
              Expect.equal vanished.Status Unstable "a report vanished after glob expansion follows Java File.length zero semantics"
              Expect.equal vanished.TestTotals (Some(1, 1, 0)) "a vanished matched path contributes one synthetic empty case"
              Expect.equal vanished.StageWarning (Some Unstable) "a vanished matched path remains an ordinary test warning"

              File.WriteAllText(ioReport, "<testsuite tests=\"1\" failures=\"0\" skipped=\"0\"/>")

              use heldOpen =
                  File.Open(ioReport, FileMode.Open, FileAccess.ReadWrite, FileShare.None)

              let ioFailure = junit "io.xml" true true None
              Expect.equal ioFailure.Status Failure "a nonzero report which cannot be opened remains terminal"
              Expect.isNone ioFailure.TestTotals "a genuine open failure publishes no summary"
              Expect.isNone ioFailure.StageWarning "a genuine open failure is not a test-failure warning"

              let postScanReport = Path.Combine(baseRequest.Workspace, "postscan.xml")
              File.WriteAllText(postScanReport, "not xml")
              let postScanPolls = ref 0

              let abortAfterScan () =
                  postScanPolls.Value <- postScanPolls.Value + 1
                  postScanPolls.Value = 3

              let interrupted = junit "postscan.xml" true true (Some abortAfterScan)
              Expect.equal postScanPolls.Value 3 "the interrupt fired only at the final post-scan poll"
              Expect.equal interrupted.Status Aborted "post-scan interruption overrides a recoverable synthetic result"
              Expect.isNone interrupted.TestTotals "an interrupted scan never publishes counts"
              Expect.isNone interrupted.StageWarning "an interrupted scan does not decorate the stage as unstable"
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
    match argv with
    | [| "--secret-umask-child"; reportFile |] ->
        runSecretUmaskChild reportFile
    | [| "--containment-child"; registry; pidFile; readyFile |] ->
        runContainmentChild registry pidFile readyFile
    | [| "--containment-exited-leader-child"; registry; pidFile; readyFile |] ->
        runExitedLeaderContainmentChild registry pidFile readyFile
    | [| "--containment-term-grace-child"; registry; pidFile; termFile |] ->
        runTermGraceContainmentChild registry pidFile termFile
    | [| "--containment-hostile-path-child"; registry; pidFile; readyFile; hostilePath |] ->
        runHostilePathContainmentChild registry pidFile readyFile hostilePath
    | [| "--containment-watchdog-revalidation-child";
          registry;
          pidFile;
          readyFile;
          termFile;
          killReleaseFile |] ->
        runWatchdogRevalidationChild registry pidFile readyFile termFile killReleaseFile
    | [| "--containment-zombie-leader-child"; registry; pidFile; readyFile; effectFile |] ->
        runZombieLeaderContainmentChild registry pidFile readyFile effectFile
    | [| "--containment-pre-setsid-child";
          registry;
          effectFile;
          releaseFile;
          readyFile;
          observedFile;
          stoppedFile;
          cleanupReleaseFile |] ->
        runPreSetsidContainmentChild
            registry
            effectFile
            releaseFile
            readyFile
            observedFile
            stoppedFile
            cleanupReleaseFile
    | _ ->
        // These tests spawn real processes and assert on /proc; running them in
        // parallel makes the survivor counts race against each other.
        runTestsWithCLIArgs
            []
            argv
            (testSequenced
                (testList
                    "Fogell.Execution"
                    [ workspaceHygiene
                      shellExecution
                      environmentIsolation
                      containment
                      eventDrivenWaits
                      credentialKeyBoundaries
                      stashDefaultExcludes
                      stashSymlinkContainment
                      secrets
                      deadProcessDetection
                      externalInterrupt
                      maskingOnOutputPath ]))
