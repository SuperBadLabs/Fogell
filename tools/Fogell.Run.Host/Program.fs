module Fogell.Run.Host.Program

open System
open System.IO
open Fogell.Domain
open Fogell.Differential
open Fogell.Journal

/// FG-112. The restart lane's HOST: runs a real Jenkinsfile through the real
/// walker with the FG-025 journal wired in, as a separate killable process —
/// the acceptance criterion is a genuine SIGKILL, and a process cannot kill
/// itself and then observe its own recovery.
///
/// Semantics on re-invocation over the same journal:
///  * a durably finished step is skipped silently (exactly-once);
///  * a step that STARTED without finishing is surfaced and the run REFUSES
///    (exit 3, steps named) — the engine genuinely does not know whether the
///    effect landed, and re-running is the at-least-once semantics ADR 0003
///    rejects;
///  * a terminal journal makes the run a no-op ("already-terminal", exit 0).
///
/// FG-046b adds the APPROVALS inbox (optional fifth argument): a controller-side
/// directory where a pending `input` publishes `<id>.pending` and an approver
/// answers with `<id>.decision` containing exactly `approve <who>` or
/// `reject <who>`, NEWLINE-TERMINATED — both fields are required and the
/// terminator is how a half-written file is told from a finished one. The answer
/// is journaled before the build acts on it, so it survives the kill, and both
/// files are deleted once it is durable.
///
/// WITHOUT the fifth argument there is no approver, and an un-timed `input`
/// fails closed with FG-046's named refusal rather than waiting for a human who
/// has nowhere to answer.
///
/// usage: fogell-run-host <jenkinsfile> <workspace-root> <job-name> <journal> [approvals-dir]
[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | jenkinsfile :: workspaceRoot :: jobName :: journalArg :: rest when List.length rest <= 1 ->
        let approvalsArg = List.tryHead rest
        // Paths first, RESOLVED once: every containment decision below compares
        // physical locations, because a symlink anywhere in the chain makes a
        // lexical prefix meaningless (the wipe follows links, the OS does not
        // care about our string arithmetic).
        //
        // EVERY existing component is resolved, walking from the root — an
        // intermediate link is invisible if only the deepest component is
        // examined and that component is not itself a link.
        let resolve (p: string) =
            // A real realpath: after substituting a link target the path is
            // DIFFERENT and its own components may be links, so resolution
            // restarts from the beginning. (Resolving each component once left
            // `/safe/link -> /safe/hop/sub` with `/safe/hop -> /srv` pointing
            // at a location the kernel would never use.) LinkTarget rather than
            // ResolveLinkTarget so DANGLING links are followed too; bounded, so
            // a cycle refuses instead of spinning.
            let mutable current = Path.GetFullPath p
            let mutable rounds = 0
            let mutable settled = false

            while not settled && rounds < 64 do
                rounds <- rounds + 1
                let parts = current.Split([| Path.DirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
                let mutable acc = string Path.DirectorySeparatorChar
                let mutable i = 0
                let mutable substituted = false

                while not substituted && i < parts.Length do
                    let candidate = Path.Combine(acc, parts[i])

                    match (FileInfo candidate).LinkTarget with
                    | null ->
                        acc <- candidate
                        i <- i + 1
                    | target ->
                        let resolvedHead =
                            if Path.IsPathRooted target then
                                Path.GetFullPath target
                            else
                                Path.GetFullPath(Path.Combine(acc, target))

                        let rest = parts[i + 1 ..] |> String.concat (string Path.DirectorySeparatorChar)
                        current <- if rest = "" then resolvedHead else Path.Combine(resolvedHead, rest)
                        substituted <- true

                if not substituted then
                    current <- acc
                    settled <- true

            if not settled then
                eprintfn $"symlink resolution did not settle for {p} (cycle?) — refusing"
                exit 2

            current

        let trimSep (p: string) =
            p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

        // Identity components are length-prefixed: a resolved path may contain
        // '|', and plain concatenation made distinct tuples compare equal.
        // The LEXICAL root joins the resolved triple: the walker exposes the
        // raw root to the build (WORKSPACE and friends derive from it), so two
        // spellings that resolve to one tree are still different builds.
        let encodeIdentity (lexical: string) (r: string) (w: string) (a: string) =
            $"{lexical.Length}:{lexical}|{r.Length}:{r}|{w.Length}:{w}|{a.Length}:{a}"

        // a relative journal path would hand Journal.ensure an empty directory
        // name and fail the first append — normalise before anything reads it
        let journalPath = Path.GetFullPath journalArg

        // The job name is also a controller-state KEY (artifacts, stashes, SCM
        // records all combine it as a relative segment), so it must be a plain
        // relative name — not rooted, not traversing.
        if Path.IsPathRooted jobName || jobName.Split([| '/'; '\\' |]) |> Array.contains ".." then
            eprintfn $"job-name must be a plain relative name (not rooted, no traversal): {jobName}"
            exit 2

        let workspaceFull = trimSep (Path.GetFullPath(Path.Combine(workspaceRoot, jobName)))
        let realWorkspace = trimSep (resolve workspaceFull)
        let realRoot = trimSep (resolve workspaceRoot)

        // the ARTIFACT root too: stashes, archived artifacts and SCM records
        // live under it, and it can be retargeted independently of the
        // workspace (a symlink of its own)
        let artifactsResolved = trimSep (resolve (Path.Combine(workspaceRoot, "_artifacts")))

        // Controller-side state must NOT live inside the workspace: stashes and
        // SCM records live under _artifacts, credential material under
        // _secrets, and the fresh-attempt wipe would destroy state a resume is
        // entitled to (or another job's secrets). EVERY controller root is
        // checked, both directions — a job named after one, or a symlinked
        // store pointing into the job tree.
        let controllerRoots =
            [ "_artifacts", artifactsResolved
              "_secrets", trimSep (resolve (Path.Combine(workspaceRoot, "_secrets"))) ]

        for name, path in controllerRoots do
            if
                path = realWorkspace
                || path.StartsWith(realWorkspace + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || realWorkspace.StartsWith(path + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
            then
                eprintfn $"the {name} store ({path}) overlaps the workspace ({realWorkspace}) — the wipe would destroy controller-side state; refusing"
                exit 2

        // ORDINAL: the default StartsWith is culture-sensitive and can treat
        // ignorable characters (a soft hyphen, say) as absent, authorising a
        // sibling directory that is not actually beneath the root.
        if not (realWorkspace.StartsWith(realRoot + string Path.DirectorySeparatorChar, StringComparison.Ordinal)) then
            eprintfn $"job-name resolves outside the workspace root ({realWorkspace} vs {realRoot}) — the wipe would delete an unrelated directory; refusing"
            exit 2

        // a journal PHYSICALLY inside the workspace would be unlinked by the
        // fresh-attempt wipe — every record then lands on an unlinked inode and
        // resume reads an empty file. Compared resolved: an aliasing symlink
        // (root=/tmp/link -> /tmp/real, journal under /tmp/real/job) is the
        // same physical location and must refuse too.
        // BOTH forms: the resolved check catches an aliasing root, the lexical
        // one catches a journal that IS a symlink sitting inside the workspace
        // (its target is controller-side, but the wipe removes the link itself)
        if
            (resolve journalPath).StartsWith(realWorkspace + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || journalPath.StartsWith(workspaceFull + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
        then
            eprintfn $"journal path is inside the workspace ({realWorkspace}) — the fresh-attempt wipe would unlink it; keep it controller-side"
            exit 2

        // FG-046b: the approvals inbox is controller-side for the same reason and
        // by the same two checks — an inbox inside the workspace would have the
        // operator's answer deleted by the fresh-attempt wipe, and a resumed
        // build would sit there waiting for a human who already answered.
        let approvalsDir = approvalsArg |> Option.map Path.GetFullPath

        match approvalsDir with
        | Some dir when
            (resolve dir).StartsWith(realWorkspace + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || (resolve dir) = realWorkspace
            || dir.StartsWith(workspaceFull + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ->
            eprintfn $"approvals directory is inside the workspace ({realWorkspace}) — the fresh-attempt wipe would delete pending answers; keep it controller-side"
            exit 2
        | _ -> ()

        // FG-046b. The id a prompt is addressed by: the SHA-256 of the
        // length-prefixed (journal, stage, index) triple — length-prefixed for
        // the same reason the workspace identity is, since either string may
        // contain the delimiter and `("a|1", 0)` must not collide with
        // `("a", 10)`. Hex-addressed rather than name-addressed also mirrors what
        // Jenkins publishes: `wfapi/nextPendingInputAction` returns a hex id and
        // the approval is POSTed to `input/<id>/proceedEmpty` (MEASURED by probe;
        // UNPROVEN BY RECEIPT, since a receipt compares two engines running one
        // Jenkinsfile and neither side can be answered from a Jenkinsfile). The
        // FULL digest is used — truncating it would reintroduce the collision the
        // prefixing just removed.
        //
        // The JOURNAL is in the preimage and that is a correctness requirement,
        // not tidiness: a journal is per-build, so keying on (stage, index) alone
        // made one human's answer address the SAME prompt in every later build.
        // Sharing one inbox across builds — the obvious way to deploy this — then
        // auto-approved them, and recorded in each journal that a named human had
        // approved a build they never saw. The id must be stable across ATTEMPTS
        // of one build (that is what lets an answer written between attempts be
        // found) and unique across builds; the journal path is exactly that.
        let actionId (stage: string) (index: int) =
            use h = Security.Cryptography.SHA256.Create()

            Text.Encoding.UTF8.GetBytes($"{journalPath.Length}:{journalPath}|{stage.Length}:{stage}|{index}")
            |> h.ComputeHash
            |> Convert.ToHexString
            |> fun x -> x.ToLowerInvariant()

        // Reads an answer out of the inbox. Returns None both for "not answered
        // yet" and for "answered in a way this host cannot read" — the caller
        // distinguishes them only to warn, because neither is an answer and
        // guessing which way a malformed one leaned would be inventing a human's
        // decision, the one thing durability exists to protect.
        let readDecision (dir: string) (stage: string) (index: int) : Result<bool * string, string> option =
            let path = Path.Combine(dir, actionId stage index + ".decision")

            if not (File.Exists path) then
                None
            else

            // An answer is observed while it is being WRITTEN (an operator's
            // `echo` is not atomic), so completeness has to be established, not
            // assumed. Two requirements, and the first one was learned the hard
            // way: `approve alice` seen after only `approve` had landed parsed as
            // a perfectly valid approval by an unnamed submitter, journaled it,
            // and never re-read the file — a scheduling accident silently erasing
            // the only audit content the record carries.
            //   1. the content must be NEWLINE-TERMINATED — the terminator is the
            //      writer's statement that it finished;
            //   2. both fields must be present — a verdict alone is not an answer.
            let contents =
                try
                    File.ReadAllText path
                with _ ->
                    ""

            if contents = "" then
                None
            elif not (contents.EndsWith "\n") then
                // said out loud rather than waited on in silence: a file the
                // writer never terminated would otherwise stall the build with
                // no diagnostic at all. Warnings dedupe on the message, so a
                // stalled file says this once.
                Some(Error $"{path} is not newline-terminated ({contents.Length} chars) — an answer must end with a newline")
            else

            let raw = contents.Trim()

            if raw = "" then
                None
            else

            match raw.Split([| ' '; '\t'; '\n' |], 2, StringSplitOptions.RemoveEmptyEntries) with
            | [| v; w |] when w.Trim() <> "" ->
                match v.Trim().ToLowerInvariant() with
                | "approve"
                | "approved" -> Some(Ok(true, w.Trim()))
                | "reject"
                | "rejected" -> Some(Ok(false, w.Trim()))
                | _ -> Some(Error $"{path} does not say approve or reject (got {raw.Length} chars)")
            | _ -> Some(Error $"{path} must say '<approve|reject> <who>' on one line (got {raw.Length} chars)")

        // An answer is CONSUMED once it is durable. Both files go: the prompt is
        // no longer outstanding, and a decision left lying in the inbox is an
        // answer waiting to be re-read by something it was never given to.
        // Called only AFTER the record is journaled and synced — in the other
        // order a kill in between would destroy the only copy of the answer.
        let consumeAnswer (dir: string) (stage: string) (index: int) =
            for suffix in [ ".pending"; ".decision" ] do
                try
                    let p = Path.Combine(dir, actionId stage index + suffix)
                    if File.Exists p then File.Delete p
                with _ ->
                    ()

        // control characters in the identity would tear the journal's wire
        // format exactly like a hostile stage name — refuse by name
        // the RESOLVED values are what get journaled: a clean lexical root can
        // resolve through a symlink into a target carrying a delimiter
        if
            [ workspaceRoot; jobName; realWorkspace; realRoot; artifactsResolved ]
            |> List.exists (fun v -> v.Contains '\t' || v.Contains '\n' || v.Contains '\r')
        then
            eprintfn "workspace path or job-name contains tab/newline/carriage-return (after symlink resolution) — unjournalable; refusing"
            exit 2

        // repair a torn tail BEFORE the plan is built: the reconciliation
        // refusal exits before any journal open, and an operator's appended
        // fix would otherwise land invisibly behind the fragment
        Journal.repairTail journalPath

        let plan = Resume.plan (Journal.read journalPath)

        // the terminal no-op must not depend on the Jenkinsfile still existing
        // (a completed build queried after rotation) — check BEFORE reading it
        match plan.Terminal with
        | Some t ->
            printfn $"already-terminal: {BuildStatus.toWireString t}"
            0
        | None ->

        let script = File.ReadAllText jenkinsfile

        let digest =
            use h = Security.Cryptography.SHA256.Create()

            Text.Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n"))
            |> h.ComputeHash
            |> Convert.ToHexString
            |> fun x -> x.ToLowerInvariant()

        // A resume against a CHANGED definition hybrid-executes two pipelines
        // over one (stage, index) key space — a changed step occupying a
        // finished key would be silently skipped. Refuse by name instead.
        match plan.ScriptDigest with
        | Some recorded when recorded <> digest ->
            eprintfn "definition-changed: the journal belongs to a different Jenkinsfile; refusing to resume a hybrid"
            4
        | _ ->

        // same shape for the WORKSPACE: durable setup steps were skipped on the
        // strength of effects that live in a particular tree
        // RESOLVED root: a symlink retargeted between attempts changes the
        // physical tree while the lexical path is unchanged, and the resumed
        // run would skip durable setup against a different directory.
        // Keyed on the resolved WORKSPACE, not just the root: a symlink inside
        // the job path retargeted between attempts (ws/job -> ws/a becoming
        // ws/job -> ws/b) keeps root and lexical name equal while the physical
        // tree changes underneath the durable steps.
        // Root AND workspace: two different roots can reach one physical job
        // directory through symlinks while the walker derives controller state
        // (artifacts, stashes, SCM records) from the ROOT — which would differ.
        // Compared against what the PREVIOUS attempt recorded; the value this
        // attempt records is computed after the wipe (below), because the wipe
        // can replace a workspace SYMLINK with a real directory and change the
        // physical target under us.
        // LENGTH-PREFIXED: a resolved path may legitimately contain '|', and
        // plain concatenation let distinct tuples collide into one string.
        let identity = encodeIdentity (trimSep (Path.GetFullPath workspaceRoot)) realRoot realWorkspace artifactsResolved

        match plan.WorkspaceIdentity with
        | Some(w, j) when w <> identity || j <> jobName ->
            eprintfn $"workspace-changed: the journal belongs to ({w}, {j}); refusing to resume against ({identity}, {jobName})"
            4
        | _ ->

        // FG-046b. An answer that arrived while NOTHING was running is still an
        // answer. Adopt it into the journal before the reconciliation decision:
        // a prompt killed unanswered and answered afterwards would otherwise be
        // reported as unreconcilable and the human asked a second time — the
        // exact failure this ticket exists to remove.
        //
        // Placed HERE, and the position is load-bearing. It writes a durable
        // human decision into the (stage, index) key space and destroys the
        // marker telling the approver the prompt still needs them, so it must
        // run only once every gate that establishes the key space is the right
        // one has passed: not terminal, digest matches, workspace matches.
        // Adopting first meant a run that then refused `definition-changed` had
        // already committed the answer to a key space it had just declared
        // untrustworthy, and had already consumed the approver's marker.
        //
        // Only interrupted `input` steps are considered, so it cannot manufacture
        // an outcome for a step whose effect is genuinely unknown.
        let plan =
            match approvalsDir with
            | None -> plan
            | Some dir ->
                let adopted =
                    plan.NeedsReconciliation
                    |> List.filter (fun k -> Set.contains k plan.InputSteps)
                    |> List.choose (fun (stage, i) ->
                        match readDecision dir stage i with
                        | Some(Ok(approved, who)) -> Some(stage, i, approved, who)
                        | Some(Error e) ->
                            eprintfn $"approvals: {e} — still waiting"
                            None
                        | None -> None)

                if List.isEmpty adopted then
                    plan
                else
                    use j = Journal.openAt journalPath EveryStep

                    for stage, i, approved, who in adopted do
                        j.Append(InputDecision(stage, i, approved, who))
                        let verdict = if approved then "approved" else "rejected"
                        printfn $"answer adopted: {stage}#{i} {verdict} by {who}"

                    j.Sync()
                    j.Close()

                    for stage, i, _, _ in adopted do
                        consumeAnswer dir stage i

                    Resume.plan (Journal.read journalPath)

        if not (List.isEmpty plan.NeedsReconciliation) then
            let named =
                plan.NeedsReconciliation
                |> List.map (fun (st, i) -> $"{st}#{i}")
                |> String.concat ", "

            eprintfn $"needs-reconciliation: {named} — a started step has no recorded outcome; refusing to guess"
            3
        else

        // a digest-only journal (died after the first sync, before any step)
        // is STILL a second attempt: the digest is written by an attempt, so
        // its presence in the plan proves one existed — without this, the
        // workspace of a real prior attempt is wiped and isRestartedRun lies
        let resuming = plan.ScriptDigest.IsSome || not (Map.isEmpty plan.Steps)

        // The HOST owns the fresh-attempt wipe, and it happens BEFORE the first
        // metadata append: a kill between metadata and a later wipe would make
        // the next invocation "resume" over a never-wiped stale tree. With this
        // order, metadata-present always implies workspace-prepared — and a
        // kill after the wipe but before metadata just wipes again, idempotent.
        if not resuming then
            // Delete the path that was VALIDATED, physically: resolving and then
            // deleting the lexical path is a check/use race — a component
            // swapped between the two would redirect the recursive delete. The
            // final component must not be a link either, or the wipe would
            // follow it rather than remove the workspace.
            if not (isNull (FileInfo workspaceFull).LinkTarget) then
                eprintfn $"workspace path {workspaceFull} is a symlink — refusing to wipe through it"
                exit 2

            if Directory.Exists realWorkspace then
                Directory.Delete(realWorkspace, true)

            Directory.CreateDirectory workspaceFull |> ignore
            // mirror runWith's fresh path: a new job has no SCM build history
            WalkerGit.resetHistory (Path.Combine(workspaceRoot, "_artifacts")) jobName

        if resuming then
            printfn "resuming: one recovery event for this build"

            printfn
                "note: build-scoped Groovy bindings assigned by skipped steps are NOT restored (step outcomes are journaled, interpreter state is not) — a later step referencing one fails by name"

        use journal = Journal.openAt journalPath EveryStep

        // first attempt records what definition this journal belongs to
        // backfilled INDEPENDENTLY: a death between the two appends must not
        // leave the missing one unrecordable forever
        if plan.ScriptDigest.IsNone then
            journal.Append(ScriptDigest digest)

        if plan.WorkspaceIdentity.IsNone then
            // recomputed post-wipe: the fresh path may have replaced a symlink
            // with a real directory, making the pre-wipe resolution stale
            let identityNow =
                encodeIdentity
                    (trimSep (Path.GetFullPath workspaceRoot))
                    (trimSep (resolve workspaceRoot))
                    (trimSep (resolve workspaceFull))
                    (trimSep (resolve (Path.Combine(workspaceRoot, "_artifacts"))))
            journal.Append(WorkspaceIdentity(identityNow, jobName))

        if plan.ScriptDigest.IsNone || plan.WorkspaceIdentity.IsNone then
            journal.Sync()

        // FG-046b. Answers already durable, seeded from the journal. This seed is
        // what makes an answer SURVIVE: after it, the inbox is not consulted
        // again for that prompt — a resumed attempt reads the human's decision
        // out of the journal even if the inbox is gone.
        let answered = Collections.Concurrent.ConcurrentDictionary<string * int, InputAnswer>()

        for KeyValue((stage, i), (approved, who)) in plan.InputDecisions do
            answered[(stage, i)] <- (if approved then InputApproved who else InputRejected who)

        // Written once per prompt (the file's existence is the marker), and once
        // per distinct malformed answer — a 250 ms poll loop would otherwise
        // print thousands of identical lines and bury the one that matters.
        let publishedPending = Collections.Concurrent.ConcurrentDictionary<string * int, bool>()
        let warnedAbout = Collections.Concurrent.ConcurrentDictionary<string, bool>()

        // `None` when no inbox was given: there is then no approver at all, and
        // the walker must say so by name rather than wait for a human who has no
        // way to answer.
        let pollInputAnswer =
            approvalsDir
            |> Option.map (fun dir ->
                fun (stage: string) (index: int) (prompt: string) ->
                    match answered.TryGetValue((stage, index)) with
                    | true, a -> Some a
                    | _ ->

                    match readDecision dir stage index with
                    | None ->
                        // publish what is being waited on, once
                        if publishedPending.TryAdd((stage, index), true) then
                            Directory.CreateDirectory dir |> ignore
                            // named in full so a human answering has to guess
                            // nothing: which prompt, in which stage, at which step
                            File.WriteAllText(
                                Path.Combine(dir, actionId stage index + ".pending"),
                                $"stage\t{stage}\nstep\t{index}\nprompt\t{prompt}\n"
                            )

                        None
                    | Some(Error e) ->
                        // Fail-closed in the FG-103 sense: an answer this host
                        // cannot READ is not an answer, and the prompt keeps
                        // waiting.
                        if warnedAbout.TryAdd(e, true) then
                            eprintfn $"approvals: {e} — still waiting"

                        None
                    | Some(Ok(approved, who)) ->
                        // DURABLE BEFORE ACTED ON. The whole ticket is this
                        // ordering: the walker proceeds the instant this returns,
                        // so appending after the fact would lose the human's
                        // answer to a kill in between and ask them again.
                        journal.Append(InputDecision(stage, index, approved, who))
                        journal.Sync()

                        let answer = if approved then InputApproved who else InputRejected who
                        answered[(stage, index)] <- answer
                        // consumed only now that it is durable: the prompt is no
                        // longer outstanding, and an answer left in the inbox is
                        // one waiting to be re-read by a build it was never given to
                        consumeAnswer dir stage index
                        Some answer)

        let hooks =
            { IsRestartedRun = resuming
              StageWasCommitted = fun stage -> Set.contains stage plan.CommittedStages
              SkippedStatus =
                fun stage i ->
                    match Resume.dispositionOf plan stage i with
                    | AlreadyFinished st -> Some st
                    | _ -> None
              ShouldExecute =
                fun stage i ->
                    let run = Resume.shouldExecute plan stage i

                    if not run then
                        printfn $"skip (durably finished): {stage}#{i}"

                    run
              OnStepStarted =
                fun stage i name ->
                    journal.Append(StepStarted(stage, i, name))
                    journal.Sync()
              OnStepFinished = fun stage i status -> journal.Append(StepFinished(stage, i, status))
              OnStageCommitted =
                fun stage ->
                    journal.Append(StageCommitted stage)
                    journal.Sync()
              PollInputAnswer = pollInputAnswer }

        // A prompt can stop being outstanding without being ANSWERED — a deadline
        // expiring, a failFast sibling. Its marker would otherwise outlive the
        // build, and `ls *.pending` is only useful to an approver if everything
        // in it is genuinely still waiting.
        let clearOutstandingPrompts () =
            match approvalsDir with
            | None -> ()
            | Some dir ->
                for KeyValue((stage, i), _) in publishedPending do
                    consumeAnswer dir stage i

        // the workspace is already prepared above — never re-wipe here
        match FogellSide.runPersisted [] workspaceRoot jobName false hooks script with
        | Result.Error e ->
            // The attempt is OVER and failed — steps may already be durably
            // finished (the leak guard, for one, refuses AFTER they ran), and
            // leaving no terminal record would let a later invocation resume
            // into a finished run. Terminal failure is the honest state.
            journal.Append(BuildFinished BuildStatus.Failure)
            journal.Close()
            clearOutstandingPrompts ()
            eprintfn $"run failed: {e}"
            2
        | Ok trace ->
            let status =
                BuildStatus.ofWireString trace.Result |> Option.defaultValue BuildStatus.Failure

            journal.Append(BuildFinished status)
            journal.Close()
            clearOutstandingPrompts ()
            printfn $"completed: {trace.Result}"
            for l in trace.Output do printfn "| %s" l
            if trace.Result = "success" then 0 else 1
    | _ ->
        eprintfn "usage: fogell-run-host <jenkinsfile> <workspace-root> <job-name> <journal> [approvals-dir]"
        2
