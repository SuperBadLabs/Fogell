namespace Fogell.Differential

open System
open System.IO
open Fogell.Domain

/// FG-111/FG-052. The `git` step: a real clone/fetch into the workspace, and
/// Jenkins' git-plugin narration in its measured wording and order.
///
/// THREE console shapes, all measured on 2.568.1 against the lane repo
/// (receipts `git-step-clone`, `git-step-refetch.b1/.b2`,
/// `git-step-default-branch`):
///  * fresh workspace — the 20-line clone shape, ending
///    `First time build. Skipping changelog.`;
///  * existing repo — the 18-line fetch shape (`Fetching changes from the
///    remote Git repository`, `git branch -D` before the re-branch, and
///    `git rev-list --no-walk <pre-fetch HEAD>` LAST). The committed receipts
///    pin the unchanged-remote variant; a probe with a commit pushed between
///    builds measured the console structurally IDENTICAL (the changelog is
///    computed, never printed by this step) — that variant is measurement,
///    not yet a sealed receipt;
///  * no `branch:` argument — Jenkins defaults to `master`, MEASURED
///    (receipt `git-step-default-branch`), which 13 of the 228 corpus files
///    rely on.
/// The shape discriminator is a real `git rev-parse --resolve-git-dir` — no
/// cross-build memory beyond the workspace the FG-110 lane persists.
///
/// Discipline: every `> git ...` narration line is printed BEFORE its command
/// runs (Jenkins' order — a failure console keeps the attempted line), every
/// exit status is checked, and the first failure stops the step with the
/// failing command NAMED. The `git --version` echo prints THIS engine's git —
/// folded two-sidedly to ${GITVERSION} by the harness, never suppressed.
module WalkerGit =

    /// One bounded git subprocess: async reads on BOTH pipes (fetch writes its
    /// progress to stderr — synchronous ReadToEnd deadlocked once the pipe
    /// buffer filled), a 10-minute wait matching the `# timeout=10` the
    /// narration promises, kill on expiry, and every start failure (missing
    /// binary included) is an Error, never an unhandled throw.
    let private git
        (env: (string * string) list)
        (waitMs: int)
        (shouldStop: unit -> bool)
        (cwd: string)
        (args: string list)
        : Result<string, string> =
        try
            let psi = Diagnostics.ProcessStartInfo("git")
            args |> List.iter psi.ArgumentList.Add
            psi.WorkingDirectory <- cwd
            // the EFFECTIVE build environment (environment/withEnv resolved) —
            // exactly what the shell path hands its subprocesses
            env |> List.iter (fun (k, v) -> psi.Environment[k] <- v)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            use p = new Diagnostics.Process(StartInfo = psi)
            let out = Text.StringBuilder()

            p.OutputDataReceived.Add(fun e ->
                if not (isNull e.Data) then
                    out.AppendLine e.Data |> ignore)
            // drained so git can never block on a full pipe; the CONSOLE lines
            // are the measured narration, never raw git chatter
            p.ErrorDataReceived.Add(fun _ -> ())

            p.Start() |> ignore
            p.BeginOutputReadLine()
            p.BeginErrorReadLine()

            // Wait in slices so an external cancellation (a failFast SIBLING —
            // ctx.Interrupt has no deadline to bound this wait with) interrupts
            // a stalled subprocess instead of riding out the whole budget.
            let clock = Diagnostics.Stopwatch.StartNew()
            let mutable exited = false
            let mutable stopped = false

            while not exited && not stopped && clock.ElapsedMilliseconds < int64 waitMs do
                exited <- p.WaitForExit 250
                if not exited then stopped <- shouldStop ()

            if not exited then
                p.Kill true
                // wait out the kill: returning while git still terminates would
                // leave it mutating the workspace after the step reported failure
                p.WaitForExit()

                if stopped then
                    Result.Error "interrupted"
                else
                    Result.Error $"timed out after {waitMs} ms"
            else
                p.WaitForExit() // flush the async handlers
                if p.ExitCode = 0 then
                    Ok(out.ToString().Trim())
                else
                    Result.Error $"exit {p.ExitCode}"
        with ex ->
            Result.Error ex.Message

    /// The last-built revision, kept CONTROLLER-side (under the artifact root,
    /// like the stash store) because that is where Jenkins keeps SCM build
    /// data: `deleteDir()` wipes the workspace, not the build history.
    /// MEASURED (receipt `git-step-deletedir.b2`): a wiped workspace on build 2
    /// gets the full CLONE shape but ends `git rev-list --no-walk <prior sha>`,
    /// NOT "First time build" — shape is workspace-keyed, the TAIL is
    /// history-keyed, and they are independent.
    ///
    /// Keyed per (url, branch) AND per build number: a build with several git
    /// steps must not read one checkout's sha as another repo's history, and a
    /// same-build re-checkout must see the PREVIOUS build's revision, not its
    /// own write from minutes ago — Jenkins' build data is frozen at build
    /// start. Reads take the newest record from an EARLIER build; writes stamp
    /// this build's number.
    let private scmKey (url: string) (branch: string) =
        use h = Security.Cryptography.SHA256.Create()

        Text.Encoding.UTF8.GetBytes $"{url}|{branch}"
        |> h.ComputeHash
        |> Convert.ToHexString
        |> fun x -> x.Substring(0, 16).ToLowerInvariant()

    let private scmDir (artifactRoot: string) (jobKey: string) =
        Path.Combine(artifactRoot, "_scm", jobKey)

    let private readPriorRevision (artifactRoot: string) (jobKey: string) (key: string) (buildNumber: int) =
        let dir = scmDir artifactRoot jobKey

        if not (Directory.Exists dir) then
            None
        else
            Directory.GetFiles(dir, $"{key}@*.revision")
            |> Array.choose (fun f ->
                let name = Path.GetFileNameWithoutExtension f

                match Int32.TryParse(name.Substring(key.Length + 1)) with
                | true, n when n < buildNumber -> Some(n, f)
                | _ -> None)
            |> Array.sortByDescending fst
            |> Array.tryHead
            |> Option.map (fun (_, f) -> (File.ReadAllText f).Trim())

    let private writeRevision (artifactRoot: string) (jobKey: string) (key: string) (buildNumber: int) (sha: string) =
        let dir = scmDir artifactRoot jobKey
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(Path.Combine(dir, $"{key}@{buildNumber}.revision"), sha)

    /// Forget a job's SCM history — called when the harness creates the job
    /// fresh, mirroring Jenkins' doDelete (history dies with the job).
    let resetHistory (artifactRoot: string) (jobKey: string) =
        let dir = scmDir artifactRoot jobKey
        if Directory.Exists dir then Directory.Delete(dir, true)

    /// Execute the step. First failure wins: later commands are skipped and the
    /// branch fails with the failing command named (fail closed — carrying on
    /// with a half-initialised repo is the silent-loss shape).
    let runStep
        (runCtx: WalkerCtx)
        (ctx: BranchCtx)
        (cwd: string)
        (deadline: Deadline option)
        (env: (string * string) list)
        (artifactRoot: string)
        (jobKey: string)
        (buildNumber: int)
        (url: string)
        (branch: string)
        : unit =
        let emit = runCtx.Emit
        let refspec = "+refs/heads/*:refs/remotes/origin/*"
        let mutable failure: string option = None
        let mutable cancelled = false

        // Each subprocess waits AT MOST the remaining enclosing deadline (a
        // `timeout` block must not be outlived by a ten-minute fetch), floored
        // at 1 so an already-expired deadline classifies rather than hangs.
        let bound () =
            WalkerCancellation.remainingMs runCtx deadline
            |> Option.map (fun r -> int (min 600_000L r))
            |> Option.defaultValue 600_000

        let shouldStop () =
            WalkerCancellation.cancellationOf runCtx ctx deadline <> Cancellation.Running

        // A slow command that SUCCEEDS after the deadline must still cancel —
        // the classification goes through the ONE model, exactly like every
        // other self-working step.
        let checkCancelled () =
            if not cancelled && failure.IsNone then
                match WalkerCancellation.cancellationOf runCtx ctx deadline with
                | Cancellation.Running -> ()
                | c ->
                    WalkerCancellation.applyCancellation runCtx ctx "git" deadline c
                    cancelled <- true

        /// Narrate (Jenkins prints the attempted line BEFORE running), then run,
        /// then check. Returns the command's stdout when it succeeded.
        let step (narration: string option) (what: string) (args: string list) : string option =
            if failure.IsSome || cancelled then
                None
            else
                narration |> Option.iter emit

                match git env (bound ()) shouldStop cwd args with
                | Ok out ->
                    checkCancelled ()
                    if cancelled then None else Some out
                | Result.Error e ->
                    checkCancelled ()

                    if not cancelled then
                        failure <- Some $"{what} ({e})"

                    None

        /// A query whose nonzero exit is an ANSWER, not a failure — exactly the
        /// two commands Jenkins runs that way (an unset config key exits 1).
        let query (narration: string) (args: string list) =
            if failure.IsNone && not cancelled then
                emit narration
                git env (bound ()) shouldStop cwd args |> ignore

        // The REAL discriminator (a `.git` that is a worktree FILE, not a
        // directory, must not read as fresh): ran silently here, narrated in
        // its measured position below when the repo exists.
        let fresh =
            match git env (bound ()) shouldStop cwd [ "rev-parse"; "--resolve-git-dir"; Path.Combine(cwd, ".git") ] with
            | Ok _ -> false
            | Result.Error _ -> true

        // Build HISTORY, independent of the workspace: the record survives
        // deleteDir() exactly as Jenkins' build data does. (In the
        // same-workspace re-fetch, the record equals the pre-fetch HEAD the
        // measurement showed — receipt `git-step-refetch.b2`.)
        let key = scmKey url branch
        let prevSha = readPriorRevision artifactRoot jobKey key buildNumber

        if failure.IsNone then
            emit "The recommended git tool is: NONE"
            emit "No credentials specified"

            if fresh then
                emit "Cloning the remote Git repository"
                emit $"Cloning repository {url}"
                step (Some $"> git init {cwd} # timeout=10") "git init" [ "init"; cwd ] |> ignore

                if failure.IsNone then
                    emit $"Fetching upstream changes from {url}"
                    emit "> git --version # timeout=10"

                step None "git --version" [ "--version" ]
                |> Option.iter (fun v -> emit $"> git --version # '{v}'")

                step
                    (Some $"> git fetch --tags --force --progress -- {url} {refspec} # timeout=10")
                    "git fetch"
                    [ "fetch"; "--tags"; "--force"; "--progress"; "--"; url; refspec ]
                |> ignore

                step
                    (Some $"> git config remote.origin.url {url} # timeout=10")
                    "git config remote.origin.url"
                    [ "config"; "remote.origin.url"; url ]
                |> ignore

                step
                    (Some $"> git config --add remote.origin.fetch {refspec} # timeout=10")
                    "git config remote.origin.fetch"
                    [ "config"; "--add"; "remote.origin.fetch"; refspec ]
                |> ignore

                if failure.IsNone then
                    emit "Avoid second fetch"
            else
                emit $"> git rev-parse --resolve-git-dir {cwd}/.git # timeout=10"
                emit "Fetching changes from the remote Git repository"

                step
                    (Some $"> git config remote.origin.url {url} # timeout=10")
                    "git config remote.origin.url"
                    [ "config"; "remote.origin.url"; url ]
                |> ignore

                if failure.IsNone then
                    emit $"Fetching upstream changes from {url}"
                    emit "> git --version # timeout=10"

                step None "git --version" [ "--version" ]
                |> Option.iter (fun v -> emit $"> git --version # '{v}'")

                step
                    (Some $"> git fetch --tags --force --progress -- {url} {refspec} # timeout=10")
                    "git fetch"
                    [ "fetch"; "--tags"; "--force"; "--progress"; "--"; url; refspec ]
                |> ignore

            let sha =
                step
                    (Some $"> git rev-parse refs/remotes/origin/{branch}^{{commit}} # timeout=10")
                    $"git rev-parse refs/remotes/origin/{branch}"
                    [ "rev-parse"; $"refs/remotes/origin/{branch}^{{commit}}" ]

            match sha with
            | None -> ()
            | Some sha ->
                emit $"Checking out Revision {sha} (refs/remotes/origin/{branch})"
                query "> git config core.sparsecheckout # timeout=10" [ "config"; "core.sparsecheckout" ]

                step (Some $"> git checkout -f {sha} # timeout=10") "git checkout -f" [ "checkout"; "-f"; sha ]
                |> ignore

                step
                    (Some "> git branch -a -v --no-abbrev # timeout=10")
                    "git branch -a"
                    [ "branch"; "-a"; "-v"; "--no-abbrev" ]
                |> ignore

                if not fresh then
                    step (Some $"> git branch -D {branch} # timeout=10") "git branch -D" [ "branch"; "-D"; branch ]
                    |> ignore

                step
                    (Some $"> git checkout -b {branch} {sha} # timeout=10")
                    "git checkout -b"
                    [ "checkout"; "-b"; branch; sha ]
                |> ignore

                step None "git log -1" [ "log"; "-1"; "--pretty=%s" ]
                |> Option.iter (fun subject ->
                    emit $"Commit message: \"{subject}\""

                    // the TAIL is history-keyed, not workspace-keyed
                    match prevSha with
                    | None -> emit "First time build. Skipping changelog."
                    | Some prev ->
                        step
                            (Some $"> git rev-list --no-walk {prev} # timeout=10")
                            "git rev-list"
                            [ "rev-list"; "--no-walk"; prev ]
                        |> ignore

                    if failure.IsNone && not cancelled then
                        writeRevision artifactRoot jobKey key buildNumber sha)

        // a cancellation already narrated and sank through the model
        if not cancelled then
            match failure with
            | None -> ()
            | Some why ->
                emit $"ERROR: git step failed at: {why}"
                ctx.Failed.Value <- true
                ctx.Sink BuildStatus.Failure
