namespace Fogell.Differential

open System
open System.IO
open System.Security.Cryptography
open System.Text
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

    /// Produce a receipt-safe representation of a Git remote. Absolute URIs are
    /// canonicalised (which percent-encodes spaces) only when every component is
    /// safe to disclose. Userinfo, query, fragment, URI path parameters and
    /// malformed/non-URI spellings are represented only by a one-way digest.
    /// Engine notes must never become a credential exfiltration path.
    let attestationUrl (url: string) =
        let opaque () =
            let digest = SHA256.HashData(Encoding.UTF8.GetBytes url)
            $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}"

        let hasStrictUtf8PercentEncoding (value: string) =
            try
                let bytes = ResizeArray<byte>()
                let mutable index = 0

                while index < value.Length do
                    if value[index] = '%' then
                        if index + 2 >= value.Length then
                            invalidArg (nameof value) "truncated percent escape"

                        bytes.Add(Convert.ToByte(value.Substring(index + 1, 2), 16))
                        index <- index + 3
                    else
                        let next = value.IndexOf('%', index)
                        let finish = if next < 0 then value.Length else next
                        Encoding.UTF8.GetBytes(value.Substring(index, finish - index))
                        |> bytes.AddRange
                        index <- finish

                let strictUtf8 = UTF8Encoding(false, true)
                strictUtf8.GetString(bytes.ToArray()) |> ignore
                true
            with _ ->
                false

        let mutable parsed = Unchecked.defaultof<Uri>

        if Uri.TryCreate(url, UriKind.Absolute, &parsed) then
            let safePath =
                if
                    Text.RegularExpressions.Regex.IsMatch(url, "%(?![0-9A-Fa-f]{2})")
                    || not (hasStrictUtf8PercentEncoding parsed.AbsolutePath)
                then
                    false
                else
                    try
                        let decoded = Uri.UnescapeDataString parsed.AbsolutePath
                        decoded.IndexOfAny [| ';'; '?'; '#' |] < 0
                    with _ ->
                        false

            if
                String.IsNullOrEmpty parsed.UserInfo
                && String.IsNullOrEmpty parsed.Query
                && String.IsNullOrEmpty parsed.Fragment
                && safePath
            then
                parsed.AbsoluteUri
            else
                opaque ()
        else
            opaque ()

    /// Identity of the exact commit used to load an SCM-defined Jenkinsfile.
    /// Captured in the same private fetch as the bytes, so early evaluation
    /// failure cannot erase which remote object Fogell actually inspected.
    type RemoteJenkinsfile =
        { Script: string
          Revision: string
          Tree: string
          JenkinsfileBlob: string }

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

    /// The last-built revision and the build result that FINALISES it, kept
    /// CONTROLLER-side (under the artifact root, like the stash store) because
    /// that is where Jenkins keeps SCM build data: `deleteDir()` wipes the
    /// workspace, not the build history.
    /// MEASURED (receipt `git-step-deletedir.b2`): a wiped workspace on build 2
    /// gets the full CLONE shape but ends `git rev-list --no-walk <prior sha>`,
    /// NOT "First time build" — shape is workspace-keyed, the TAIL is
    /// history-keyed, and they are independent.
    ///
    /// Keyed per (url, branch) AND per build number: a build with several git
    /// steps must not read one checkout's sha as another repo's history, and a
    /// same-build re-checkout must see the PREVIOUS build's revision, not its
    /// own write from minutes ago — Jenkins' build data is frozen at build
    /// start. A revision written by a build that crashed before producing a
    /// terminal result is not history. Reads therefore take only records from
    /// FINALIZED earlier builds; writes stamp this build's number and become
    /// visible only when `finalizeBuild` publishes its terminal-result marker.
    let private scmKey (url: string) (branch: string) =
        use h = Security.Cryptography.SHA256.Create()

        Text.Encoding.UTF8.GetBytes $"{url}|{branch}"
        |> h.ComputeHash
        |> Convert.ToHexString
        |> fun x -> x.Substring(0, 16).ToLowerInvariant()

    let private scmDir (artifactRoot: string) (jobKey: string) =
        Path.Combine(artifactRoot, "_scm", jobKey)

    // Parallel branches can check out the same (url, branch) concurrently and
    // would race File.WriteAllText on the same record; one writer would take an
    // IOException. Coarse but correct: record IO is tiny and rare.
    let private recordLock = obj ()

    let private resultMarker (artifactRoot: string) (jobKey: string) (buildNumber: int) =
        Path.Combine(scmDir artifactRoot jobKey, $"build@{buildNumber}.result")

    let private parseFinalResult (path: string) =
        try
            match (File.ReadAllText path).Trim().Split '\t' with
            | [| "fogell-scm-build-result-v1"; value |] ->
                BuildStatus.ofWireString value
                |> Option.filter BuildStatus.isTerminal
            | _ -> None
        with _ ->
            // A torn or otherwise unreadable marker is not a completed build.
            // Silently inventing history here can select the wrong pipeline
            // branch; absence keeps the SCM map honest and fail-closed.
            None

    type private PriorHistory =
        { Previous: string option
          PreviousSuccessful: string option
          LatestStatus: BuildStatus option }

    let private emptyPriorHistory =
        { Previous = None
          PreviousSuccessful = None
          LatestStatus = None }

    let private readPriorHistory (artifactRoot: string) (jobKey: string) (key: string) (buildNumber: int) =
        lock recordLock (fun () ->
            let dir = scmDir artifactRoot jobKey

            if not (Directory.Exists dir) then
                Ok emptyPriorHistory
            else
                let candidates =
                    Directory.GetFiles(dir, $"{key}@*.revision")
                    |> Array.choose (fun revisionPath ->
                        let name = Path.GetFileNameWithoutExtension revisionPath

                        match Int32.TryParse(name.Substring(key.Length + 1)) with
                        | true, n when n < buildNumber -> Some(n, revisionPath)
                        | _ -> None)

                let inspected =
                    candidates
                    |> Array.map (fun (n, revisionPath) ->
                        let marker = resultMarker artifactRoot jobKey n

                        if not (File.Exists marker) then
                            Error $"SCM retained history is incomplete: build {n} has a revision without a finalized result"
                        else
                            match parseFinalResult marker with
                            | None ->
                                Error $"SCM retained history is corrupt: build {n} has an unreadable finalized-result marker"
                            | Some result ->
                                try
                                    let revision = (File.ReadAllText revisionPath).Trim()

                                    if Text.RegularExpressions.Regex.IsMatch(revision, "^[0-9a-fA-F]{40}$") then
                                        Ok(n, revision, result)
                                    else
                                        Error $"SCM retained history is corrupt: build {n} has an invalid revision"
                                with _ ->
                                    Error $"SCM retained history is corrupt: build {n} has an unreadable revision")

                match inspected |> Array.tryPick (function Error why -> Some why | Ok _ -> None) with
                | Some why -> Error why
                | None ->
                    let finalized =
                        inspected
                        |> Array.choose (function Ok entry -> Some entry | Error _ -> None)
                        |> Array.sortByDescending (fun (n, _, _) -> n)

                    Ok
                        { Previous =
                            finalized
                            |> Array.tryHead
                            |> Option.map (fun (_, revision, _) -> revision)
                          PreviousSuccessful =
                            finalized
                            |> Array.tryFind (fun (_, _, result) -> result = BuildStatus.Success)
                            |> Option.map (fun (_, revision, _) -> revision)
                          LatestStatus =
                            finalized
                            |> Array.tryHead
                            |> Option.map (fun (_, _, result) -> result) })

    let private writeRevision (artifactRoot: string) (jobKey: string) (key: string) (buildNumber: int) (sha: string) =
        lock recordLock (fun () ->
            let dir = scmDir artifactRoot jobKey
            Directory.CreateDirectory dir |> ignore
            File.WriteAllText(Path.Combine(dir, $"{key}@{buildNumber}.revision"), sha))

    /// Publish the terminal result that makes every SCM revision captured by
    /// this build eligible for later history. The marker is job-wide because a
    /// build has one terminal result even when it checks out several remotes.
    /// A build that captured no SCM revision is a no-op: ordinary inline jobs
    /// must not manufacture an `_scm` directory merely because every run has a
    /// terminal status.
    /// Same-result repetition is idempotent (resume/finally paths may converge
    /// here); a conflicting terminal result is refused rather than rewriting
    /// history after a later build may already have consumed it.
    let finalizeBuild
        (artifactRoot: string)
        (jobKey: string)
        (buildNumber: int)
        (result: BuildStatus)
        =
        if buildNumber <= 0 then
            invalidArg (nameof buildNumber) "SCM build number must be positive"

        if not (BuildStatus.isTerminal result) then
            invalidArg (nameof result) "SCM history can be finalized only with a terminal build result"

        lock recordLock (fun () ->
            let dir = scmDir artifactRoot jobKey
            if
                not (Directory.Exists dir)
                || Directory.GetFiles(dir, $"*@{buildNumber}.revision").Length = 0
            then
                ()
            else
                let marker = resultMarker artifactRoot jobKey buildNumber
                let body = $"fogell-scm-build-result-v1\t{BuildStatus.toWireString result}"

                if File.Exists marker then
                    match parseFinalResult marker with
                    | Some existing when existing = result -> ()
                    | Some existing ->
                        invalidOp
                            $"SCM build {buildNumber} is already finalized as {BuildStatus.toWireString existing}, not {BuildStatus.toWireString result}"
                    | None ->
                        invalidOp $"SCM build {buildNumber} has an unreadable finalized-result marker"
                else
                    // Same-directory move is atomic. A process death before the
                    // move leaves no visible finalized marker; after it, readers
                    // see the complete versioned payload.
                    let pending = Path.Combine(dir, $".build@{buildNumber}.{Guid.NewGuid():N}.result.tmp")

                    try
                        File.WriteAllText(pending, body)
                        File.Move(pending, marker)
                    finally
                        if File.Exists pending then File.Delete pending)

    /// FG-052: the changelog TAIL (First-time / rev-list) narrates ONCE per
    /// build for GitSCM checkouts — the build's first checkout says it, later
    /// checkouts stop after "Commit message" (measured: the explicit
    /// `checkout scm` after the Declarative auto-stage has NO tail).
    let private tailMarker (artifactRoot: string) (jobKey: string) (buildNumber: int) =
        Path.Combine(scmDir artifactRoot jobKey, $"changelog-narrated@{buildNumber}")

    /// ATOMIC claim: check-and-create under ONE lock acquisition — a separate
    /// check followed by a later mark let two parallel `checkout scm` branches
    /// both pass the check and narrate the once-per-build tail twice.
    let private tryClaimTail (artifactRoot: string) (jobKey: string) (buildNumber: int) =
        lock recordLock (fun () ->
            let f = tailMarker artifactRoot jobKey buildNumber

            if File.Exists f then
                false
            else
                Directory.CreateDirectory(scmDir artifactRoot jobKey) |> ignore
                File.WriteAllText(f, "")
                true)

    /// Forget a job's SCM history — called when the harness creates the job
    /// fresh, mirroring Jenkins' doDelete (history dies with the job).
    let resetHistory (artifactRoot: string) (jobKey: string) =
        lock recordLock (fun () ->
            let dir = scmDir artifactRoot jobKey
            if Directory.Exists dir then Directory.Delete(dir, true))

    /// The two checkout dialects, both measured:
    ///  * GitStep — the `git` step: re-branches (branch -a / branch -D /
    ///    checkout -b) and always narrates the changelog tail;
    ///  * GitScm — `checkout scm` and the Declarative auto-stage: leaves a
    ///    DETACHED head (no re-branch cluster) and narrates the tail once per
    ///    build (receipts `checkout-scm-*`).
    type Style =
        | GitStep
        | GitScm

    /// FG-177. The closed return of the two measured Git producers. `Entries`
    /// is the exact immutable TreeMap projection licensed by the retained
    /// oracle: producer-specific base keys plus the two history keys together,
    /// or neither history key on the branch's first finalized observation.
    /// `Revision` remains explicit because auto-checkout also uses it for the
    /// Declarative GIT_* environment wrapper; callers must not recover it by
    /// guessing at a map key.
    type CheckoutResult =
        { Revision: string
          Entries: Map<string, string> }

    let private resultEntries
        (style: Style)
        (url: string)
        (branch: string)
        (revision: string)
        (history: PriorHistory)
        =
        let baseEntries =
            match style with
            | GitStep ->
                [ "GIT_BRANCH", $"origin/{branch}"
                  "GIT_COMMIT", revision
                  "GIT_LOCAL_BRANCH", branch
                  "GIT_URL", url ]
            | GitScm ->
                [ "GIT_BRANCH", $"origin/{branch}"
                  "GIT_COMMIT", revision
                  "GIT_URL", url ]

        let historyEntries =
            [ history.Previous
              |> Option.map (fun previous -> "GIT_PREVIOUS_COMMIT", previous)
              history.PreviousSuccessful
              |> Option.map (fun successful -> "GIT_PREVIOUS_SUCCESSFUL_COMMIT", successful) ]
            |> List.choose id

        Map.ofList (baseEntries @ historyEntries)

    /// Execute the step. First failure wins: later commands are skipped and the
    /// branch fails with the failing command named (fail closed — carrying on
    /// with a half-initialised repo is the silent-loss shape).
    let runWithStyle
        (style: Style)
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
        : CheckoutResult option =
        let emit = runCtx.Emit
        let refspec = "+refs/heads/*:refs/remotes/origin/*"
        let mutable failure: string option = None
        let mutable cancelled = false
        let mutable checkedOutSha: string option = None
        let mutable deleteExistingLocalBranch = false

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

        /// A query whose NONZERO EXIT is an answer, not a failure — exactly the
        /// two commands Jenkins runs that way (an unset config key exits 1).
        /// Everything else — timeout, interrupt, a git that cannot start — is a
        /// real breakdown and fails like any other command.
        let query (narration: string) (what: string) (args: string list) =
            if failure.IsNone && not cancelled then
                emit narration

                match git env (bound ()) shouldStop cwd args with
                | Ok _ -> checkCancelled ()
                | Result.Error e when e.StartsWith "exit " -> checkCancelled ()
                | Result.Error e ->
                    checkCancelled ()

                    if not cancelled then
                        failure <- Some $"{what} ({e})"

        // Build HISTORY, independent of the workspace: the record survives
        // deleteDir() exactly as Jenkins' build data does. (In the
        // same-workspace re-fetch, the record equals the pre-fetch HEAD the
        // measurement showed — receipt `git-step-refetch.b2`.)
        let key = scmKey url branch
        let historyResult = readPriorHistory artifactRoot jobKey key buildNumber
        let history = historyResult |> Result.defaultValue emptyPriorHistory

        // The retained oracle licenses exactly these predecessor states. It
        // did not measure a branch whose first checkout finished non-success,
        // nor the SCM-map behavior after UNSTABLE/ABORTED. Refuse BEFORE even
        // the silent rev-parse discriminator runs: an unlicensed history must
        // not fetch, initialise or otherwise mutate the caller's workspace.
        failure <-
            match historyResult with
            | Error why -> Some why
            | Ok history ->
                match history.Previous, history.PreviousSuccessful, history.LatestStatus with
                | None, None, None -> None
                | Some _, Some _, Some BuildStatus.Success
                | Some _, Some _, Some BuildStatus.Failure -> None
                | _, _, Some BuildStatus.Unstable ->
                    Some "SCM retained history is outside the measured contract: latest finalized predecessor is unstable"
                | _, _, Some BuildStatus.Aborted ->
                    Some "SCM retained history is outside the measured contract: latest finalized predecessor is aborted"
                | Some _, None, _ ->
                    Some
                        "SCM retained history is outside the measured contract: GIT_PREVIOUS_COMMIT exists without GIT_PREVIOUS_SUCCESSFUL_COMMIT"
                | None, Some _, _ ->
                    Some
                        "SCM retained history is corrupt: GIT_PREVIOUS_SUCCESSFUL_COMMIT exists without GIT_PREVIOUS_COMMIT"
                | _, _, Some BuildStatus.Success
                | _, _, Some BuildStatus.Failure ->
                    Some "SCM retained history has finalized status without the complete measured history pair"
                | _, _, Some BuildStatus.NotBuilt
                | _, _, None ->
                    Some "SCM retained history has no trustworthy finalized predecessor result"

        // The REAL discriminator (a `.git` that is a worktree FILE, not a
        // directory, must not read as fresh): ran silently here, narrated in
        // its measured position below when the repo exists. It is deliberately
        // skipped when the history boundary above refuses the checkout.
        let fresh =
            if failure.IsSome then
                false
            else
                match git env (bound ()) shouldStop cwd [ "rev-parse"; "--resolve-git-dir"; Path.Combine(cwd, ".git") ] with
                | Ok _ -> false
                | Result.Error _ -> true

        if failure.IsNone then
            // Dialect-INDEPENDENT since the lab's git-tool descriptor was first
            // exercised (measured post-restart 2026-08-01: both the git step
            // and checkout scm print it; the pre-initialization state the first
            // git-step receipts captured no longer exists and they are
            // re-sealed against the restart-stable lab).
            emit "Selected Git installation does not exist. Using Default"

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

            // The git step deletes the TARGET local branch only when that
            // branch already exists. The retained main->feature switch has a
            // reused repository but no local feature branch, and Jenkins goes
            // straight from `branch -a` to `checkout -b` there. Treat exit 1
            // from the silent existence query as absence; transport/cancel
            // failures still fail the checkout.
            if style = GitStep && not fresh && failure.IsNone && not cancelled then
                match git env (bound ()) shouldStop cwd [ "show-ref"; "--verify"; "--quiet"; $"refs/heads/{branch}" ] with
                | Ok _ ->
                    checkCancelled ()
                    deleteExistingLocalBranch <- not cancelled
                | Result.Error e when e.StartsWith "exit " -> checkCancelled ()
                | Result.Error e ->
                    checkCancelled ()
                    if not cancelled then failure <- Some $"git show-ref local branch ({e})"

            let sha =
                step
                    (Some $"> git rev-parse refs/remotes/origin/{branch}^{{commit}} # timeout=10")
                    $"git rev-parse refs/remotes/origin/{branch}"
                    [ "rev-parse"; $"refs/remotes/origin/{branch}^{{commit}}" ]

            match sha with
            | None -> ()
            | Some sha ->
                checkedOutSha <- Some sha
                emit $"Checking out Revision {sha} (refs/remotes/origin/{branch})"
                query
                    "> git config core.sparsecheckout # timeout=10"
                    "git config core.sparsecheckout"
                    [ "config"; "core.sparsecheckout" ]

                step (Some $"> git checkout -f {sha} # timeout=10") "git checkout -f" [ "checkout"; "-f"; sha ]
                |> ignore

                if style = GitStep then
                    step
                        (Some "> git branch -a -v --no-abbrev # timeout=10")
                        "git branch -a"
                        [ "branch"; "-a"; "-v"; "--no-abbrev" ]
                    |> ignore

                    if deleteExistingLocalBranch then
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

                    // the TAIL is history-keyed, not workspace-keyed — and for
                    // GitScm it narrates only on the build's FIRST checkout
                    // (atomic claim: parallel checkouts cannot both narrate)
                    let narrateTail =
                        style = GitStep || tryClaimTail artifactRoot jobKey buildNumber

                    if narrateTail then
                        match history.Previous with
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

        let completed =
            if failure.IsNone && not cancelled then
                checkedOutSha
                |> Option.map (fun revision ->
                    { Revision = revision
                      Entries = resultEntries style url branch revision history })
            else
                None

        match completed, Environment.GetEnvironmentVariable "FOGELL_SCM_ATTESTATION" with
        | Some checkout, "fg177-probes-v1" ->
            runCtx.NoteEngine $"git-checkout branch={branch} revision={checkout.Revision} url={attestationUrl url}"
        | _ -> ()

        completed

    /// FG-052. Read the Jenkinsfile an SCM branch currently serves — the bytes
    /// Jenkins executes. Used by the harness's fail-closed drift check, which
    /// must hold for EVERY SCM case (including skipDefaultCheckout, where no
    /// workspace checkout exists to compare against). Shallow fetch into a
    /// throwaway dir; any failure is an Error the caller refuses on.
    let readRemoteJenkinsfile (url: string) (branch: string) : Result<RemoteJenkinsfile, string> =
        let tmp = Path.Combine(Path.GetTempPath(), "fogell-scm-verify-" + Guid.NewGuid().ToString "N")

        try
            try
                Directory.CreateDirectory tmp |> ignore
                let noStop () = false

                let run what args =
                    match git [] 600_000 noStop tmp args with
                    | Ok out -> Ok out
                    | Result.Error e -> Result.Error $"{what} ({e})"

                run "git init" [ "init"; tmp ]
                |> Result.bind (fun _ -> run "git fetch" [ "fetch"; "--depth"; "1"; "--"; url; branch ])
                |> Result.bind (fun _ ->
                    run "git rev-parse commit" [ "rev-parse"; "FETCH_HEAD^{commit}" ]
                    |> Result.bind (fun revision ->
                        run "git rev-parse tree" [ "rev-parse"; "FETCH_HEAD^{tree}" ]
                        |> Result.bind (fun tree ->
                            run "git rev-parse Jenkinsfile" [ "rev-parse"; "FETCH_HEAD:Jenkinsfile" ]
                            |> Result.bind (fun blob ->
                                run "git show Jenkinsfile" [ "show"; "FETCH_HEAD:Jenkinsfile" ]
                                |> Result.map (fun script ->
                                    { Script = script
                                      Revision = revision
                                      Tree = tree
                                      JenkinsfileBlob = blob })))))
            with ex ->
                Result.Error ex.Message
        finally
            try
                Directory.Delete(tmp, true)
            with _ ->
                ()

    /// The `git` step's public face. Its closed result is the step's value; it
    /// does not export GIT_* to later steps merely by returning those entries.
    let runStep runCtx ctx cwd deadline env artifactRoot jobKey buildNumber url branch =
        runWithStyle GitStep runCtx ctx cwd deadline env artifactRoot jobKey buildNumber url branch

    /// FG-052. `checkout scm` and the Declarative auto-checkout stage.
    let runCheckout runCtx ctx cwd deadline env artifactRoot jobKey buildNumber (scm: ScmSpec) =
        runWithStyle GitScm runCtx ctx cwd deadline env artifactRoot jobKey buildNumber scm.Url scm.Branch
