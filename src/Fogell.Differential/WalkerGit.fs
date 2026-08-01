namespace Fogell.Differential

open System
open System.IO
open Fogell.Domain

/// FG-111/FG-052. The `git` step: a real clone/fetch into the workspace, and
/// Jenkins' git-plugin narration in its measured wording and order.
///
/// THREE console shapes, all measured on 2.568.1 against the lane repo
/// (receipts `git-step-clone`, `git-step-refetch`):
///  * fresh workspace (no .git) — the 20-line clone shape, ending
///    `First time build. Skipping changelog.`;
///  * existing repo, any commit movement — the 18-line fetch shape
///    (`Fetching changes from the remote Git repository`, `git branch -D`
///    before the re-branch, and `git rev-list --no-walk <pre-fetch HEAD>` as
///    the LAST line). Measured twice: with the remote unchanged and with a
///    commit pushed between builds, the console is structurally IDENTICAL —
///    the changelog is computed but never printed by this step.
/// The discriminator is exactly "does `<ws>/.git` exist" — no cross-build
/// memory beyond the workspace the FG-110 lane already persists.
///
/// The `git --version` echo prints THIS engine's git — an
/// environment-of-necessity pair like HOME, folded two-sidedly by the
/// harness's ${GITVERSION} replacement, never suppressed.
module WalkerGit =

    /// Run one git command quietly; the CONSOLE lines are the measured
    /// narration, never raw git output. Returns (exitCode, stdout).
    let private git (cwd: string) (args: string list) : int * string =
        let psi = Diagnostics.ProcessStartInfo("git")
        args |> List.iter psi.ArgumentList.Add
        psi.WorkingDirectory <- cwd
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        use p = Diagnostics.Process.Start psi
        let out = p.StandardOutput.ReadToEnd()
        p.StandardError.ReadToEnd() |> ignore
        p.WaitForExit()
        p.ExitCode, out.Trim()

    let private gitVersionLine =
        lazy
            (let code, out = git "." [ "--version" ]
             if code = 0 then out else "git version unknown")

    /// Execute the step. Emits the measured narration; a git failure FAILS the
    /// branch with the failing command named (fail closed — carrying on with a
    /// half-initialised repo is the silent-loss shape).
    let runStep
        (runCtx: WalkerCtx)
        (ctx: BranchCtx)
        (cwd: string)
        (url: string)
        (branch: string)
        : unit =
        let emit = runCtx.Emit

        let fail (what: string) =
            emit $"ERROR: git step failed at: {what}"
            ctx.Failed.Value <- true
            ctx.Sink BuildStatus.Failure

        let run (what: string) (args: string list) : string option =
            let code, out = git cwd args
            if code = 0 then Some out else None

        let refspec = "+refs/heads/*:refs/remotes/origin/*"

        emit "The recommended git tool is: NONE"
        emit "No credentials specified"

        let fresh = not (Directory.Exists(Path.Combine(cwd, ".git")))

        // pre-fetch HEAD is what the closing rev-list names on a re-fetch
        let prevSha =
            if fresh then None
            else git cwd [ "rev-parse"; "HEAD" ] |> fun (c, o) -> (if c = 0 then Some o else None)

        let prepared =
            if fresh then
                emit "Cloning the remote Git repository"
                emit $"Cloning repository {url}"

                match run "git init" [ "init"; cwd ] with
                | None ->
                    fail "git init"
                    false
                | Some _ ->
                    emit $"> git init {cwd} # timeout=10"
                    emit $"Fetching upstream changes from {url}"
                    emit "> git --version # timeout=10"
                    emit $"> git --version # '{gitVersionLine.Value}'"

                    match run "git fetch" [ "fetch"; "--tags"; "--force"; "--progress"; "--"; url; refspec ] with
                    | None ->
                        fail "git fetch"
                        false
                    | Some _ ->
                        emit $"> git fetch --tags --force --progress -- {url} {refspec} # timeout=10"

                        run "git config remote" [ "config"; "remote.origin.url"; url ] |> ignore
                        emit $"> git config remote.origin.url {url} # timeout=10"
                        run "git config fetch" [ "config"; "--add"; "remote.origin.fetch"; refspec ] |> ignore
                        emit $"> git config --add remote.origin.fetch {refspec} # timeout=10"
                        emit "Avoid second fetch"
                        true
            else
                emit $"> git rev-parse --resolve-git-dir {cwd}/.git # timeout=10"
                emit "Fetching changes from the remote Git repository"
                run "git config remote" [ "config"; "remote.origin.url"; url ] |> ignore
                emit $"> git config remote.origin.url {url} # timeout=10"
                emit $"Fetching upstream changes from {url}"
                emit "> git --version # timeout=10"
                emit $"> git --version # '{gitVersionLine.Value}'"

                match run "git fetch" [ "fetch"; "--tags"; "--force"; "--progress"; "--"; url; refspec ] with
                | None ->
                    fail "git fetch"
                    false
                | Some _ ->
                    emit $"> git fetch --tags --force --progress -- {url} {refspec} # timeout=10"
                    true

        if prepared then
            match run "git rev-parse" [ "rev-parse"; $"refs/remotes/origin/{branch}^{{commit}}" ] with
            | None -> fail $"git rev-parse refs/remotes/origin/{branch}"
            | Some sha ->
                emit $"> git rev-parse refs/remotes/origin/{branch}^{{commit}} # timeout=10"
                emit $"Checking out Revision {sha} (refs/remotes/origin/{branch})"
                emit "> git config core.sparsecheckout # timeout=10"

                match run "git checkout" [ "checkout"; "-f"; sha ] with
                | None -> fail "git checkout -f"
                | Some _ ->
                    emit $"> git checkout -f {sha} # timeout=10"
                    emit "> git branch -a -v --no-abbrev # timeout=10"

                    if not fresh then
                        run "git branch -D" [ "branch"; "-D"; branch ] |> ignore
                        emit $"> git branch -D {branch} # timeout=10"

                    match run "git checkout -b" [ "checkout"; "-b"; branch; sha ] with
                    | None -> fail "git checkout -b"
                    | Some _ ->
                        emit $"> git checkout -b {branch} {sha} # timeout=10"

                        let subject =
                            git cwd [ "log"; "-1"; "--pretty=%s" ] |> fun (c, o) -> (if c = 0 then o else "")

                        emit $"Commit message: \"{subject}\""

                        if fresh then
                            emit "First time build. Skipping changelog."
                        else
                            match prevSha with
                            | Some prev -> emit $"> git rev-list --no-walk {prev} # timeout=10"
                            | None -> fail "pre-fetch HEAD unavailable for rev-list narration"
