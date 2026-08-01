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
    let private git (cwd: string) (args: string list) : Result<string, string> =
        try
            let psi = Diagnostics.ProcessStartInfo("git")
            args |> List.iter psi.ArgumentList.Add
            psi.WorkingDirectory <- cwd
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

            if not (p.WaitForExit 600_000) then
                p.Kill true
                Result.Error "timed out after 10 minutes"
            else
                p.WaitForExit() // flush the async handlers
                if p.ExitCode = 0 then
                    Ok(out.ToString().Trim())
                else
                    Result.Error $"exit {p.ExitCode}"
        with ex ->
            Result.Error ex.Message

    /// Execute the step. First failure wins: later commands are skipped and the
    /// branch fails with the failing command named (fail closed — carrying on
    /// with a half-initialised repo is the silent-loss shape).
    let runStep
        (runCtx: WalkerCtx)
        (ctx: BranchCtx)
        (cwd: string)
        (url: string)
        (branch: string)
        : unit =
        let emit = runCtx.Emit
        let refspec = "+refs/heads/*:refs/remotes/origin/*"
        let mutable failure: string option = None

        /// Narrate (Jenkins prints the attempted line BEFORE running), then run,
        /// then check. Returns the command's stdout when it succeeded.
        let step (narration: string option) (what: string) (args: string list) : string option =
            if failure.IsSome then
                None
            else
                narration |> Option.iter emit

                match git cwd args with
                | Ok out -> Some out
                | Result.Error e ->
                    failure <- Some $"{what} ({e})"
                    None

        /// A query whose nonzero exit is an ANSWER, not a failure — exactly the
        /// two commands Jenkins runs that way (an unset config key exits 1).
        let query (narration: string) (args: string list) =
            if failure.IsNone then
                emit narration
                git cwd args |> ignore

        // The REAL discriminator (a `.git` that is a worktree FILE, not a
        // directory, must not read as fresh): ran silently here, narrated in
        // its measured position below when the repo exists.
        let fresh =
            match git cwd [ "rev-parse"; "--resolve-git-dir"; Path.Combine(cwd, ".git") ] with
            | Ok _ -> false
            | Result.Error _ -> true

        // A repo whose pre-fetch HEAD cannot be read would only fail AFTER the
        // fetch mutated it and 16 lines of success narration printed — refuse
        // UP FRONT instead, by name.
        let prevSha =
            if fresh then
                None
            else
                match git cwd [ "rev-parse"; "HEAD" ] with
                | Ok sha -> Some sha
                | Result.Error e ->
                    failure <- Some $"pre-fetch HEAD unreadable in an existing repo ({e})"
                    None

        if failure.IsNone then
            emit "The recommended git tool is: NONE"
            emit "No credentials specified"

            if fresh then
                emit "Cloning the remote Git repository"
                emit $"Cloning repository {url}"
                step (Some $"> git init {cwd} # timeout=10") "git init" [ "init"; cwd ] |> ignore
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

                    if fresh then
                        emit "First time build. Skipping changelog."
                    else
                        match prevSha with
                        | Some prev ->
                            step
                                (Some $"> git rev-list --no-walk {prev} # timeout=10")
                                "git rev-list"
                                [ "rev-list"; "--no-walk"; prev ]
                            |> ignore
                        | None -> ())

        match failure with
        | None -> ()
        | Some why ->
            emit $"ERROR: git step failed at: {why}"
            ctx.Failed.Value <- true
            ctx.Sink BuildStatus.Failure
