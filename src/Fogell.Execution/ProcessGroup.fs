namespace Fogell.Execution

open System
open System.Diagnostics
open System.Threading

/// FG-031/FG-032. Process-group lifecycle containment.
///
/// The mechanism: every step is launched through `setsid`, so the child leads a
/// new session and its pid IS its process-group id. Signalling `-pgid` then
/// reaches the step and every descendant it spawned, which is the difference
/// between terminating a step and orphaning its children.
///
/// ADR 0008 is explicit that this is *lifecycle* containment, not a hostile
/// multi-tenant boundary. A determined workload can leave the group with its own
/// `setsid`; untrusted multi-tenant work needs VM-level isolation.
type Outcome =
    | Completed of exitCode: int
    /// FG-033. The step's process was terminated by a signal from OUTSIDE this
    /// engine — an operator, an OOM killer, a container stop.
    ///
    /// Jenkins takes ~10 minutes to conclude anything here and then reports
    /// `exit code -1` with no mention of why (JB-DUR-005). Fogell owns the
    /// process, so it knows immediately and names the signal.
    | Signalled of signal: int
    | TimedOut
    | Cancelled

type Termination =
    { /// True when SIGTERM alone was enough — i.e. the step had a chance to
      /// clean up, which is the contract scripts rely on (ADR 0005).
      GracefulExit: bool
      /// True when SIGKILL was needed after the grace period elapsed.
      Escalated: bool
      /// Descendants still alive after the group was reaped. Should be zero:
      /// Jenkins leaves `nohup`ed children running and we promised to beat that.
      /// -1 means the check itself was UNAVAILABLE (the /proc read failed) — an
      /// unknown is reported as unknown, never as a clean zero.
      LeakedProcesses: int }

/// Why [waitForProcessExit] returned — decided ONCE, when the wait ends.
type internal WaitEnd =
    | Waiting
    | Exited
    | Expired
    | Interrupted

type RunResult =
    { Outcome: Outcome
      Stdout: string
      Stderr: string
      DurationMs: int64
      ProcessGroupId: int option
      Termination: Termination option }

type RunRequest =
    { Command: string
      WorkingDirectory: string
      Environment: (string * string) list
      /// Milliseconds. int64 because a Jenkins `timeout(time: 30, unit: 'DAYS')`
      /// exceeds Int32.MaxValue ms and must not be silently shortened.
      TimeoutMs: int64 option
      /// How long a step may take to honour SIGTERM before it is killed.
      GraceMs: int
      /// Called with each output line as it arrives, so a running build streams
      /// rather than materialising at the end (FG-040 / JB-LOG-002 parity).
      OnLine: (string -> unit) option
      /// Set when the step's group should be reaped even on success. Jenkins does
      /// NOT do this — measured: `nohup`ed children survive both success and
      /// abort, and JENKINS_NODE_COOKIE=dontKillMe is moot because nothing is
      /// killed. FG-032 beats that, with an opt-out.
      ReapGroup: bool
      /// FG-036. Polled while the step runs; when it returns true the step is
      /// interrupted from OUTSIDE — a `failFast` sibling failing, or an operator
      /// abort. It takes the same SIGTERM -> grace -> SIGKILL path a timeout
      /// takes, because JB-FAIL-003 measured Jenkins using ONE interrupt
      /// mechanism for both, and a script's trap handler cannot tell them apart.
      /// The outcome is [Cancelled], not [TimedOut]: the step did not run out of
      /// time, so reporting a timeout would misattribute the cause.
      Interrupt: (unit -> bool) option }

    static member create(command, workingDirectory) =
        { Command = command
          WorkingDirectory = workingDirectory
          Environment = []
          TimeoutMs = None
          GraceMs = 2_000
          OnLine = None
          ReapGroup = true
          Interrupt = None }

module ProcessGroup =

    /// Count processes still in the group. Reads /proc directly rather than
    /// shelling out, so the check cannot itself spawn something.
    /// -1 means UNKNOWN: the /proc read itself failed. FG-103 — returning 0 there
    /// made the leak check a gate that could not fail: a broken /proc reported
    /// "nothing survived" while FG-032's headline claim rested on this number, and
    /// both unit tests asserting 0 passed against a completely dead reader.
    /// Unknown fails CLOSED everywhere downstream: the group is treated as still
    /// populated, and the diagnostic says the check was unavailable rather than
    /// inventing a clean bill.
    let private survivorsIn (pgid: int) : int =
        try
            IO.Directory.GetDirectories "/proc"
            |> Array.choose (fun d ->
                match Int32.TryParse(IO.Path.GetFileName d) with
                | true, pid -> Some pid
                | _ -> None)
            |> Array.filter (fun pid ->
                match Native.processGroupOf pid with
                | Some g -> g = pgid
                | None -> false)
            |> Array.length
        with _ ->
            -1

    /// Wait until the group is EMPTY, up to `budgetMs`.
    ///
    /// This deliberately counts group membership rather than asking whether the
    /// leader pid still exists. The leader is usually the first to exit — a step
    /// that backgrounds a daemon leaves the group populated while the leader is
    /// long gone — so a leader-existence check reports success and leaves the
    /// daemon running. That is exactly the Jenkins behaviour FG-032 exists to
    /// beat, and the first version of this function reproduced it.
    let private waitForGroupExit (pgid: int) (budgetMs: int) : bool =
        let sw = Stopwatch.StartNew()
        let mutable gone = survivorsIn pgid = 0

        while not gone && sw.ElapsedMilliseconds < int64 budgetMs do
            Thread.Sleep 20
            gone <- survivorsIn pgid = 0

        gone

    /// SIGTERM the group, wait out the grace period, then SIGKILL. This is the
    /// contract measured on Jenkins (JB-FAIL-003): the interrupt is a trappable
    /// TERM with a grace window, and scripts install handlers expecting it.
    let terminateGroup (pgid: int) (graceMs: int) : Termination =
        let termDelivered = Native.signalGroup pgid Native.SIGTERM
        let exitedOnTerm = termDelivered && waitForGroupExit pgid graceMs

        let escalated =
            if exitedOnTerm then
                false
            else
                Native.signalGroup pgid Native.SIGKILL |> ignore
                waitForGroupExit pgid 2_000 |> ignore
                true

        { GracefulExit = exitedOnTerm
          Escalated = escalated
          LeakedProcesses = survivorsIn pgid }

    /// Reap whatever remains of a group after the step's direct child exited.
    /// A step that backgrounds a daemon leaves it in the group; Jenkins lets it
    /// survive, we do not.
    let reap (pgid: int) (graceMs: int) : Termination =
        if survivorsIn pgid = 0 then
            { GracefulExit = true
              Escalated = false
              LeakedProcesses = 0 }
        else
            terminateGroup pgid graceMs

    /// Run one command in its own process group.
    let run (request: RunRequest) : RunResult =
        let sw = Stopwatch.StartNew()

        // `setsid --wait` keeps a parent around to collect the exit status, but
        // that parent is what .NET reports as the process id — and ITS group is
        // ours, not the child's. So the session leader reports its own pid on
        // stderr as the first line, and that is the real process-group id.
        let pgidMarker = "__FOGELL_PGID "

        let psi = ProcessStartInfo("/usr/bin/setsid")
        psi.ArgumentList.Add "--wait"
        psi.ArgumentList.Add "/bin/sh"
        psi.ArgumentList.Add "-c"
        // `-xe`, exactly as Jenkins' durable-task runs a shell step: `-x` makes the
        // trace an EMITTED, COMPARED artifact on both engines (retiring the last
        // wording-only suppression and FG-002c's continuation gap with it), and
        // `-e` is the errexit semantics the receipts were already measured against.
        // `2>&1` merges the streams IN THE SHELL: the trace goes to stderr, and
        // .NET's two async pipe readers deliver cross-stream events in racy order —
        // output lines overtook their own trace. One pipe is kernel-ordered, and it
        // is also exactly what Jenkins' console is. The pgid marker stays on the
        // OUTER stderr, printed before the exec redirects anything.
        psi.ArgumentList.Add $"printf '%%s%%s\\n' '{pgidMarker}' \"$$\" >&2; exec /bin/sh -xec \"$FOGELL_SCRIPT\" 2>&1"
        psi.Environment["FOGELL_SCRIPT"] <- request.Command
        psi.WorkingDirectory <- request.WorkingDirectory
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false

        for k, v in request.Environment do
            psi.Environment[k] <- v

        use proc = new Process()
        proc.StartInfo <- psi

        let stdout = Text.StringBuilder()
        let stderr = Text.StringBuilder()
        let reportedPgid = ref 0

        let emit (sink: Text.StringBuilder) (line: string) =
            if line <> null then
                lock sink (fun () -> sink.AppendLine line |> ignore)
                request.OnLine |> Option.iter (fun f -> f line)

        proc.OutputDataReceived.Add(fun e -> emit stdout e.Data)

        proc.ErrorDataReceived.Add(fun e ->
            match e.Data with
            | null -> ()
            | line when line.StartsWith pgidMarker ->
                // the leader's own pid: the real group id. Never surfaced to the
                // caller as build output.
                match Int32.TryParse(line.Substring(pgidMarker.Length).Trim()) with
                | true, pid -> reportedPgid.Value <- pid
                | _ -> ()
            | line -> emit stderr line)

        proc.Start() |> ignore
        proc.BeginOutputReadLine()
        proc.BeginErrorReadLine()

        // Wait briefly for the leader to report its pid. Deriving it from
        // proc.Id is WRONG: that is setsid's pid, in our own group.
        let pgid =
            let clock = Stopwatch.StartNew()

            while reportedPgid.Value = 0 && clock.ElapsedMilliseconds < 2_000L && not proc.HasExited do
                Thread.Sleep 5

            // one more chance after exit, in case the marker arrived late
            if reportedPgid.Value = 0 then
                Thread.Sleep 50

            match reportedPgid.Value with
            | 0 -> None
            | pid -> Some pid

        // NOTE: never use the parameterless WaitForExit() with redirected
        // output. It waits for the *pipes* to close, and a backgrounded
        // grandchild inherits them — so a step that spawns a daemon would hang
        // the executor forever. Wait on the process handle only, then give the
        // async readers a bounded window to flush.
        let interrupted () =
            match request.Interrupt with
            | Some p -> (try p () with _ -> false)
            | None -> false

        // The CAUSE is decided once, at the moment the wait ends — re-sampling
        // `interrupted()` afterwards raced a sibling failing just after a deadline
        // expiry, flipping both the narration and the reported outcome.
        let waitForProcessExit (budgetMs: int64 option) =
            let deadline =
                budgetMs |> Option.map (fun ms -> Stopwatch.StartNew(), ms)

            let mutable exited = proc.HasExited

            let expired () =
                match deadline with
                | Some(clock, ms) -> clock.ElapsedMilliseconds >= ms
                | None -> false

            let mutable cause = if exited then WaitEnd.Exited else WaitEnd.Waiting

            while cause = WaitEnd.Waiting do
                if proc.HasExited then cause <- WaitEnd.Exited
                elif expired () then cause <- WaitEnd.Expired
                elif interrupted () then cause <- WaitEnd.Interrupted
                else Thread.Sleep 10

            cause

        let flushReaders (budgetMs: int) =
            // Best-effort: if a daemon holds the pipe this returns on the budget
            // rather than blocking, and whatever was read so far is kept.
            let clock = Stopwatch.StartNew()
            let mutable settled = false

            while not settled && clock.ElapsedMilliseconds < int64 budgetMs do
                let before = stdout.Length + stderr.Length
                Thread.Sleep 40
                settled <- (stdout.Length + stderr.Length) = before

        let waitEnd = waitForProcessExit request.TimeoutMs
        let finished = waitEnd = WaitEnd.Exited

        let outcome, termination =
            if finished then
                flushReaders 500
                let code = proc.ExitCode

                let t =
                    if request.ReapGroup then
                        pgid |> Option.map (fun g -> reap g request.GraceMs)
                    else
                        None

                // On Linux a process killed by signal N exits 128+N.
                //
                // REVIEW FIXES (Codex P2 + Copilot, PR #11), both correct:
                //  * the range stopped at 164, covering signals 1..36, but Linux
                //    has signals up to 64 (exit 192), so a high-numbered signal was
                //    silently reported as an ordinary exit code;
                //  * this is a HEURISTIC and cannot be otherwise here. `setsid
                //    --wait` propagates "the same return", so the wait-status bit
                //    that distinguishes "killed by signal 9" from "exited 137" is
                //    already gone by the time .NET reports ExitCode. A script that
                //    deliberately calls `exit 137` is indistinguishable from one
                //    that was SIGKILLed. The diagnostic must therefore say "likely",
                //    and FG-033's claim is corrected accordingly — see the board.
                let outcome =
                    if code > 128 && code <= 192 then Signalled(code - 128) else Completed code

                outcome, t
            else
                // Jenkins' interrupt narration, in its words and its order — measured
                // on 2.568.1 — so the two logs COMPARE (FG-102) instead of each
                // engine's version being suppressed. `Terminated` then arrives from
                // the shell itself on both engines. The cause is the SNAPSHOT taken
                // when the wait ended, never a fresh sample.
                if waitEnd = WaitEnd.Expired then
                    request.OnLine |> Option.iter (fun f -> f "Cancelling nested steps due to timeout")

                request.OnLine |> Option.iter (fun f -> f "Sending interrupt signal to process")

                let t = pgid |> Option.map (fun g -> terminateGroup g request.GraceMs)
                flushReaders 300

                // On Jenkins the wrapper shell survives to print `Terminated` for its
                // killed child; Fogell's SIGTERM reaches the WHOLE group, so nobody is
                // left alive to say it — the engine says it, in the same position.
                request.OnLine |> Option.iter (fun f -> f "Terminated")
                // Distinguish the two ways a step can fail to finish. Both take
                // the same signal path; only the reported cause differs.
                (if waitEnd = WaitEnd.Interrupted then Cancelled else TimedOut), t

        sw.Stop()

        { Outcome = outcome
          Stdout = stdout.ToString()
          Stderr = stderr.ToString()
          DurationMs = sw.ElapsedMilliseconds
          ProcessGroupId = pgid
          Termination = termination }
