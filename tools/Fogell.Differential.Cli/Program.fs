module Fogell.Differential.Cli.Program

open System
open System.IO
open Fogell.Differential
open Fogell.Execution

/// FG-002. Runs one or more Jenkinsfiles through BOTH engines and seals a
/// receipt per file.
///
/// Usage:
///   fogell-diff <jenkins-url> <jenkins-core> <receipt-dir> <file.Jenkinsfile>...
///
/// Environment:
///   FOGELL_JENKINS_WORKSPACE  host path of Jenkins' workspace root (optional;
///                             without it, workspace hashes are not compared)
///   FOGELL_JENKINS_RAW_CONSOLE_JOB / _BUILD / _PATH
///                             optional all-or-none exact build console export;
///                             PATH must be absolute, its parent must already
///                             exist, contain no symlink/reparse component, and
///                             not name a directory. The file is atomically
///                             replaced; export failure aborts the run.
[<EntryPoint>]
let main argv =
    match Array.toList argv with
    // FG-161. Recompute every receipt's seal from the receipt itself.
    //
    // A MODE ON THIS CLI, not a reimplementation in the scorecard generator: the hash
    // rule stays in `Compare.sealedContent`, the one place that also writes it. A
    // babashka copy would be a fourth copy of a rule this file already watched drift.
    //
    // Needs no Jenkins, no corpus and no case files, so it runs in CI where the full
    // differential cannot.
    | "--verify-seals" :: rest ->
        let dir =
            match rest with
            | d :: _ -> d
            | [] -> "differential/receipts"

        if not (Directory.Exists dir) then
            eprintfn $"verify-seals: no such directory: {dir}"
            2
        else
            let receipts = Directory.GetFiles(dir, "*.receipt.txt") |> Array.sort

            // A directory with no receipts is a FAILURE, not a vacuous pass. The check
            // would otherwise report success loudest at the moment it was checking
            // nothing — a wrong path, an unbuilt tree, a renamed directory.
            if receipts.Length = 0 then
                eprintfn $"verify-seals: no receipts in {dir} — refusing to report a vacuous pass"
                2
            else
                let failures =
                    receipts
                    |> Array.choose (fun path ->
                        match Compare.verifySealedText (Path.GetFileName path) (File.ReadAllText path) with
                        | Compare.SealValid -> None
                        | bad -> Some(Path.GetFileName path, bad.Describe))

                for name, why in failures do
                    eprintfn $"  {name}: {why}"

                if failures.Length = 0 then
                    printfn
                        $"seal verification: {receipts.Length} receipt(s) recomputed from their own content, all match"

                    0
                else
                    eprintfn $"SEAL VERIFICATION FAILED: {failures.Length} of {receipts.Length} receipt(s)"
                    1

    | baseUrl :: core :: receiptDir :: (_ :: _ as files) ->
        match Jenkins.validateUniqueCaseJobs files with
        | Ok() -> ()
        | Error why ->
            eprintfn $"case identity refused: {why}"
            exit 2

        let jenkinsWorkspace =
            match Environment.GetEnvironmentVariable "FOGELL_JENKINS_WORKSPACE" with
            | null | "" -> None
            | v -> Some v

        let collector =
            match Environment.GetEnvironmentVariable "FOGELL_JENKINS_WORKSPACE_CMD" with
            | null | "" -> None
            | v -> Some v

        let rawConsoleExport =
            let job = Environment.GetEnvironmentVariable "FOGELL_JENKINS_RAW_CONSOLE_JOB"
            let build = Environment.GetEnvironmentVariable "FOGELL_JENKINS_RAW_CONSOLE_BUILD"
            let path = Environment.GetEnvironmentVariable "FOGELL_JENKINS_RAW_CONSOLE_PATH"
            let present value = not (String.IsNullOrEmpty value)

            match present job, present build, present path with
            | false, false, false -> None
            | true, true, true ->
                match Int32.TryParse build with
                | true, buildNumber
                    when buildNumber > 0
                         && Text.RegularExpressions.Regex.IsMatch(
                             job,
                             "^[A-Za-z0-9][A-Za-z0-9._-]*$"
                         )
                         && Path.IsPathFullyQualified path ->
                    Some
                        { JobName = job
                          BuildNumber = buildNumber
                          Path = Path.GetFullPath path
                          Observed = false }
                | _ ->
                    eprintfn
                        "raw-console export refused: JOB must be one safe Jenkins job name, BUILD a positive integer, and PATH absolute"

                    exit 2
                    None
            | _ ->
                eprintfn
                    "raw-console export refused: FOGELL_JENKINS_RAW_CONSOLE_JOB, _BUILD and _PATH must be configured together"

                exit 2
                None

        // ONE coordinated canonicalisation set for BOTH traces: the union of each
        // engine's inherited values for the curated names, so a literal equal to
        // either engine's value rewrites identically on both sides.
        // No collector → NO inherited-env canonicalisation at all: rewriting only
        // Fogell's values while Jenkins' stayed literal manufactured divergences
        // whenever the optional variable was simply not set. Both sides or neither.
        let mutable envCanonicalisationEnabled = true

        let jenkinsEnv =
            match Environment.GetEnvironmentVariable "FOGELL_JENKINS_ENV_CMD" with
            | null
            | "" ->
                envCanonicalisationEnabled <- false
                []
            | cmd ->
                // FAIL CLOSED (FG-103): a CONFIGURED collector that cannot deliver
                // is a broken harness, and continuing with a partial replacement set
                // would manufacture divergences with no indication why. Refuse the
                // whole run instead.
                let refuse (why: string) =
                    eprintfn $"env collector failed: {why}; refusing to run with a partial canonicalisation set"
                    exit 2

                try
                    let psi = Diagnostics.ProcessStartInfo("/bin/sh")
                    psi.ArgumentList.Add "-c"
                    psi.ArgumentList.Add cmd
                    psi.RedirectStandardOutput <- true
                    psi.UseShellExecute <- false
                    use p = Diagnostics.Process.Start psi
                    // bounded: WAIT first, then read — ReadToEnd on a stalled ssh
                    // held the pipe open forever and the 30 s timeout never applied
                    let reader = p.StandardOutput.ReadToEndAsync()

                    if not (p.WaitForExit 30_000) then
                        (try p.Kill(true) with _ -> ())
                        refuse "timed out after 30 s"

                    if p.ExitCode <> 0 then refuse $"exit code {p.ExitCode}"

                    let out = if reader.Wait 5_000 then reader.Result else refuse "output never arrived"; ""

                    let parsed =
                        out.Split '\n'
                        |> Array.choose (fun line ->
                            match line.IndexOf '=' with
                            | i when i > 0 -> Some(line.Substring(0, i), line.Substring(i + 1).Trim())
                            | _ -> None)
                        |> Array.toList

                    if List.isEmpty parsed then refuse "no NAME=VALUE lines in output"
                    parsed
                with
                | :? System.ComponentModel.Win32Exception as e -> refuse e.Message; []
                | :? AggregateException as e -> refuse e.InnerException.Message; []

        // Canonicalisation stays INJECTIVE by canonicalising only names the CASE
        // actually references: both engines share the script text, so `$PATH` in it
        // means every occurrence of either engine's PATH value is an expansion —
        // while a case that merely PRINTS a path literal gets no rewriting and a
        // cross-engine literal collision diverges visibly. (The residual — a script
        // that both references $NAME and prints the other engine's literal value —
        // requires deliberate construction and is accepted, stated here.)
        // The env set applies only to XTRACE rows (see Trace), which is what made
        // three rounds of reference-gate lexing unnecessary: expansions live on
        // engine-generated lines, literals live in output, and neither needs the
        // script parsed. The union set stays injective via the ambiguity dropper.
        // The build-visible HOME is derived from this explicit run root, so the
        // controller's TMPDIR must not select it.  /tmp is part of the Linux
        // agent contract just like the fixed build PATH.
        let fogellRoot =
            Path.Combine("/tmp", "fogell-diff-" + Guid.NewGuid().ToString("N").Substring(0, 8))

        Directory.CreateDirectory(
            fogellRoot,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

        // HOME is build-scoped and injected into the enabled HOME fold by the
        // Fogell runner once job/build identity is known. PATH remains static.
        let fogellBuildBaseline =
            LaunchEnvironment.buildBaseline ""
            |> List.filter (fun (name, _) -> name <> "HOME")

        let envReplacements =
            if not envCanonicalisationEnabled then
                []
            else
                Fogell.Differential.Trace.canonicalisedEnvNames
                |> List.collect (fun name ->
                    [ match jenkinsEnv |> List.tryFind (fun (n, _) -> n = name) with
                      | Some(_, v) when v <> "" -> yield v, "${" + name + "}"
                      | _ -> ()

                      match fogellBuildBaseline |> List.tryFind (fun (n, _) -> n = name) with
                      | Some(_, value) -> yield value, "${" + name + "}"
                      | None -> () ])
                |> List.distinct
                |> List.groupBy fst
                |> List.choose (fun (_, pairs) ->
                    match pairs |> List.map snd |> List.distinct with
                    | [ _ ] -> Some pairs.Head
                    | _ -> None)

        // FG-111. The `git` step echoes each engine's OWN `git --version` — an
        // environment-of-necessity pair exactly like HOME. Both versions fold to
        // ${GITVERSION} through the same two-sided compare rule (and the fold is
        // listed in any receipt that used it). Both sides or neither, and a
        // CONFIGURED collector that cannot deliver refuses the run (FG-103).
        let gitVersionReplacements =
            match Environment.GetEnvironmentVariable "FOGELL_JENKINS_GIT_VERSION_CMD" with
            | null
            | "" -> []
            | cmd ->
                let refuse (why: string) =
                    eprintfn $"git version collector failed: {why}; refusing a one-sided fold"
                    exit 2

                let collect (exe: string) (args: string list) =
                    try
                        let psi = Diagnostics.ProcessStartInfo(exe)
                        args |> List.iter psi.ArgumentList.Add
                        psi.RedirectStandardOutput <- true
                        psi.UseShellExecute <- false
                        use p = new Diagnostics.Process(StartInfo = psi)
                        let sb = Text.StringBuilder()

                        // ASYNC read + bounded wait: a stalled ssh that never
                        // closes stdout hangs a synchronous ReadToEnd forever,
                        // and wait-then-read deadlocks on a full pipe — the two
                        // failure shapes WalkerGit's runner closes, closed here.
                        p.OutputDataReceived.Add(fun e ->
                            if not (isNull e.Data) then
                                sb.AppendLine e.Data |> ignore)

                        p.Start() |> ignore
                        p.BeginOutputReadLine()

                        if not (p.WaitForExit 30_000) then
                            p.Kill(true)
                            p.WaitForExit()
                            refuse "timed out"

                        // flush the async handlers before reading — a fast exit
                        // can beat the last OutputDataReceived callback
                        p.WaitForExit()

                        let out = sb.ToString().Trim()
                        if p.ExitCode <> 0 || out = "" then refuse $"exit {p.ExitCode}"
                        out
                    with ex ->
                        refuse ex.Message
                        ""

                let jenkinsGit = collect "/bin/sh" [ "-c"; cmd ]
                let localGit = collect "git" [ "--version" ]

                // SCOPED to the plugin's narration line (Codex P1, twice —
                // round 1's "fix" for this never reached the tree): folding the
                // raw version string also collapses a build's OWN
                // `sh 'git --version'` stdout, manufactured equality on output
                // a lift-and-shift user genuinely sees differ. The full-line
                // shape can only match engine narration; raw version text in
                // build output DIVERGES visibly.
                let shape (v: string) = $"> git --version # '{v}'"

                [ shape jenkinsGit, "> git --version # '${GITVERSION}'"
                  shape localGit, "> git --version # '${GITVERSION}'" ]
                |> List.distinct

        // through the SAME ambiguity dropper as the env pairs: an env value that
        // literally equals a `git version ...` string must drop out, not become
        // one key with two tokens decided by replacement order
        let envReplacementsAll =
            envReplacements @ gitVersionReplacements
            |> List.distinct
            |> List.groupBy fst
            |> List.choose (fun (_, pairs) ->
                match pairs |> List.map snd |> List.distinct with
                | [ _ ] -> Some pairs.Head
                | _ -> None)

        let cfg =
            { BaseUrl = baseUrl
              CoreVersion = core
              WorkspaceRoot = jenkinsWorkspace
              WorkspaceCollector = collector
              RawConsoleExport = rawConsoleExport
              // per-case; replaced at each call site from that case's script
              DeclaresTimestamps = false }

        printfn "jenkins:   %s (core %s)" baseUrl core
        printfn
            "workspace: %s"
            (match jenkinsWorkspace, collector with
             | Some p, _ -> $"local {p}"
             | None, Some c -> $"remote collector: {c.Substring(0, min 60 c.Length)}..."
             | None, None -> "<not collected — workspace hashes NOT compared>")
        printfn ""

        // FG-110. A case whose file contains `//// NEXT BUILD ////` separator
        // lines is a SEQUENCE: its scripts run as consecutive builds of ONE job
        // (workspace and build history persist; each build's result is the next
        // build's `previous`). Every build gets its own full receipt, named
        // `<case>.b<N>`, and counts in the totals like any other case.
        let buildSeparator =
            Text.RegularExpressions.Regex(@"(?m)^//// NEXT BUILD ////\s*$")

        // A synthesized sequence name is also a VALID input filename, so two
        // sources can map to one receipt path. Silent overwrite would count both
        // while keeping one; a collision is a configuration error, said so.
        let sealedPaths = System.Collections.Generic.HashSet<string>()

        // FG-119. Cases that diverged and then did not reproduce. Held so the
        // summary NAMES them with the divergence text that was seen: a receipt
        // sealed from a re-run must never imply the first run was clean, and a
        // recovery nobody is told about is indistinguishable from a passing case.
        let recoveredCases =
            System.Collections.Generic.List<string * string list * int * int>()

        let receipts =
            files
            |> List.collect (fun file ->
                let name = Path.GetFileName file
                // ONE read for both: the bytes that get SEALED are the bytes that were
                // decoded and EXECUTED. Two reads could straddle an edit mid-run, sealing
                // content no engine saw. Bytes rather than the decoded string, because the
                // seal must move when the file does; `Compare.receipt` hashes them itself,
                // so this cannot pass a wrong digest.
                let caseBytes, script = Compare.readCaseSnapshot file

                // FG-053. Read off the SCRIPT and given to BOTH engines. Nothing
                // in a line's shape distinguishes the engine's timestamp prefix
                // from a build printing one of its own, so normalisation is told
                // rather than left to guess — and it must be told the same thing
                // on both sides or the comparison is between two different rules.
                // Job names MUST be derived from the file, not its index: an
                // index-derived name reshuffles when a case is added, and the
                // new occupant inherits the previous run's workspace. That
                // showed up as a phantom workspace-hash divergence.
                let job = Jenkins.jobNameForCase file

                // Wipe any stale workspace so a hash can only match on merit.
                //
                // FG-119: this MUST run before EVERY attempt, not once per case.
                // The retry loop re-runs a diverging case, and Fogell starts each
                // attempt from a fresh workspace while Jenkins keeps its own — so a
                // wipe hoisted out of the loop left attempts 2 and 3 comparing dirty
                // Jenkins state against clean Fogell state, and could CONFIRM a
                // divergence that the retry itself manufactured. That is the exact
                // inverse of the flake the retry exists to remove, and it would have
                // been indistinguishable from a real finding.
                let wipeJenkinsWorkspace () =
                    match Environment.GetEnvironmentVariable "FOGELL_JENKINS_WIPE_CMD" with
                    | null
                    | "" -> ()
                    | template ->
                        // FAILS CLOSED. `WaitForExit 30_000 |> ignore` swallowed both the
                        // timeout and a nonzero exit — tolerable when this ran once as a
                        // hygiene step, NOT once the retry loop made it load-bearing:
                        // a silently failed wipe puts dirty Jenkins state against fresh
                        // Fogell state, which is the divergence-manufacturing bug moving
                        // the wipe into the loop was meant to prevent. On timeout the
                        // process is also killed, or it would still be deleting the
                        // workspace while the next attempt writes into it.
                        let psi = Diagnostics.ProcessStartInfo("/bin/sh")
                        psi.ArgumentList.Add "-c"
                        psi.ArgumentList.Add(template.Replace("{job}", job))
                        psi.RedirectStandardOutput <- true
                        psi.RedirectStandardError <- true
                        use p = Diagnostics.Process.Start psi

                        // DRAIN BEFORE WAITING. Both streams are redirected, so a wipe
                        // that writes more than a pipe buffer blocks in the child while
                        // the parent waits — and the fail-closed timeout above then turns
                        // a working command into an aborted suite. Reproduced by the
                        // verifier with FOGELL_JENKINS_WIPE_CMD='yes | head -c 200000'.
                        //
                        // The deadlock predates the hardening; `|> ignore` merely hid it,
                        // proceeding with an UNWIPED workspace instead. Making it loud was
                        // right, but loud-and-wrong is still wrong.
                        let outTask = p.StandardOutput.ReadToEndAsync()
                        let errTask = p.StandardError.ReadToEndAsync()

                        if not (p.WaitForExit 30_000) then
                            (try p.Kill true with _ -> ())

                            failwith
                                $"FOGELL_JENKINS_WIPE_CMD timed out after 30s for job {job}; a retry would compare dirty Jenkins state against fresh Fogell state"

                        if p.ExitCode <> 0 then
                            let out = try outTask.Result with _ -> "<unread>"
                            let err = try errTask.Result with _ -> "<unread>"

                            failwith
                                $"FOGELL_JENKINS_WIPE_CMD exited {p.ExitCode} for job {job}; a retry would compare dirty Jenkins state against fresh Fogell state.\nstdout: {out}\nstderr: {err}"

                // FG-052. A case whose FIRST LINE is `//// SCM JOB ////` runs as an
                // SCM-DEFINED job: the sync script pushed its body to the fixture
                // repo branch `case/<stem>` (scripts/sync-scm-cases.bb), the Jenkins
                // job points at that SCM, and the Fogell side receives the same
                // bytes plus the ScmSpec `checkout scm` resolves against.
                let scmMarker = "//// SCM JOB ////"
                let isScmCase = script.StartsWith scmMarker

                let scripts =
                    if isScmCase then
                        match script.IndexOf '\n' with
                        | -1 -> [ "" ] // marker-only: caught as malformed below, by name
                        | i -> [ script.Substring(i + 1) ]
                    else
                        buildSeparator.Split script |> Array.toList

                // FG-053. Derived from the BODIES, after SCM-marker stripping and
                // sequence splitting — the whole case FILE is not a Jenkinsfile:
                // an SCM case starts with a marker line and a sequence carries
                // `//// NEXT BUILD ////` separators, so parsing the file gave
                // `false` for both and Jenkins was told something Fogell was not.
                //
                // Every body must AGREE. Jenkins.runMany takes one config for the
                // whole sequence, so a sequence that declares the option in some
                // builds and not others cannot be represented — it fails NAMED
                // below rather than silently using the first body's answer.
                let declaresTimestampsPerBody =
                    scripts
                    |> List.map (fun body ->
                        match Fogell.Pipeline.Parser.Parser.parse body with
                        | Ok p -> p.Options |> List.exists (fun o -> o.Name = "timestamps")
                        | Error _ -> false)

                let caseCfg =
                    { cfg with
                        DeclaresTimestamps =
                            declaresTimestampsPerBody |> List.exists id }

                // A malformed case must fail NAMED, not as an opaque parse error:
                // empty sequence segments, a marker with no body, or an SCM case
                // that also declares NEXT BUILD separators (SCM sequences are not
                // wired — FogellSide.runMany passes Scm = None deliberately).
                let mixedTimestamps =
                    declaresTimestampsPerBody |> List.distinct |> List.length > 1

                let malformed =
                    mixedTimestamps
                    || (List.length scripts > 1 && scripts |> List.exists String.IsNullOrWhiteSpace)
                    || (isScmCase
                        && (String.IsNullOrWhiteSpace scripts.Head
                            || buildSeparator.IsMatch scripts.Head))

                let scmSpec =
                    if isScmCase then
                        let url =
                            match Environment.GetEnvironmentVariable "FOGELL_SCM_URL" with
                            | null
                            | "" -> "git://100.105.179.51/repo.git"
                            | u -> u

                        let defaultBranch = $"case/{Path.GetFileNameWithoutExtension name}"
                        let pinnedBranch = Environment.GetEnvironmentVariable "FOGELL_SCM_PINNED_BRANCH"
                        let pinnedRevision = Environment.GetEnvironmentVariable "FOGELL_SCM_PINNED_REVISION"

                        let branch =
                            match pinnedBranch, pinnedRevision with
                            | null, null
                            | "", "" -> defaultBranch
                            | branch, revision
                                when not (String.IsNullOrEmpty branch)
                                     && not (String.IsNullOrEmpty revision)
                                     && Text.RegularExpressions.Regex.IsMatch(revision, "^[0-9a-f]{40}$")
                                     && branch = $"fogell-pins/{revision}" ->
                                branch
                            | _ ->
                                failwith
                                    "FOGELL_SCM_PINNED_BRANCH and FOGELL_SCM_PINNED_REVISION must name one matching content-addressed pin"

                        Some { Url = url; Branch = branch }
                    else
                        None

                // FG-119. A case is run, and if it DIVERGES it is run again before
                // any verdict is sealed. `dash` writes an `sh -x` trace line in more
                // than one write(), so two stages of a shell pipeline interleave
                // character-by-character (`+ ls out` + `+ wc -l` -> `+ + lswc -l out`).
                // Both engines do it — container dash 5/200 runs, local ~8%. UNPROVEN BY
                // RECEIPT and it cannot be otherwise: this is a property of the SHELL,
                // below the level a differential case can observe. Reproduced by
                // scripts/measure-xtrace-race.sh, which is the evidence —
                // so this is not a Fogell defect to repair but an unreliable ORACLE,
                // and roughly one to two cases per 106-case suite were diverging on it.
                //
                // Confirmation by repetition, NOT relaxation. A pattern-match on
                // 'looks interleaved' would also swallow a real divergence that happened
                // to contain a mid-line `+ ` — and `+ echo a + b` is an ordinary trace.
                // A divergence that reproduces is real and still fails; one that does
                // not is reported RECOVERED with the original text, never silently.
                let runBothEngines () =
                    wipeJenkinsWorkspace ()

                    let jenkinsRuns, fogellRuns =
                        if malformed then
                            let e =
                                Result.Error (
                                    if mixedTimestamps then
                                        "unsupported sequence: some builds declare options { timestamps() } and others do not — Jenkins takes one config for the whole sequence, so the two engines could not be told the same thing"
                                    elif isScmCase then
                                        "malformed SCM case: empty body or //// NEXT BUILD //// separators (SCM sequences are not supported)"
                                    else
                                        "malformed sequence file: empty build segment around a //// NEXT BUILD //// separator"
                                )
                            scripts |> List.map (fun _ -> e), scripts |> List.map (fun _ -> e)
                        else
                            match scmSpec with
                            | Some spec ->
                                Jenkins.runMany caseCfg envReplacementsAll job [ FromScm spec ],
                                [ FogellSide.runScm envReplacementsAll fogellRoot job spec scripts.Head ]
                            | None ->
                                Jenkins.runMany caseCfg envReplacementsAll job (scripts |> List.map Inline),
                                FogellSide.runMany envReplacementsAll fogellRoot job scripts

                    jenkinsRuns, fogellRuns

                let solo = List.length scripts = 1

                let caseNameFor bi =
                    if solo then
                        name
                    else
                        // suffix inserted before the FINAL extension (never
                        // string-Replace, which rewrites every occurrence and
                        // no-ops on other extensions, sealing N builds over one receipt)
                        let stem = Path.GetFileNameWithoutExtension name
                        let ext = Path.GetExtension name
                        $"{stem}.b{bi + 1}{ext}"

                let buildReceipts () =
                    let jenkinsRuns, fogellRuns = runBothEngines ()

                    List.zip jenkinsRuns fogellRuns
                    |> List.mapi (fun bi (jenkins, fogell) ->
                        let receiptEnvReplacements =
                            envReplacementsAll
                            @ FogellSide.coordinatedAgentHomeReplacement
                                envReplacementsAll
                                fogellRoot
                                job
                                (bi + 1)
                            |> List.distinct
                            |> List.groupBy fst
                            |> List.choose (fun (_, pairs) ->
                                match pairs |> List.map snd |> List.distinct with
                                | [ _ ] -> Some pairs.Head
                                | _ -> None)

                        let stableReceiptEnvironment =
                            receiptEnvReplacements
                            |> List.filter (fun (_, token) -> token = "${HOME}")

                        Compare.receiptWithStableEnvironment
                            stableReceiptEnvironment
                            (caseNameFor bi)
                            caseBytes
                            core
                            receiptEnvReplacements
                            jenkins
                            fogell)

                let anyDiverged rs =
                    rs |> List.exists (fun (r: Receipt) ->
                        match r.Verdict with
                        | Diverged _ -> true
                        | _ -> false)

                // Keyed BY BUILD INDEX. A `//// NEXT BUILD ////` sequence seals one
                // receipt per build, and stamping one flat list onto all of them made
                // every sibling claim "this case DIVERGED" when a single build had —
                // with nothing to say which. Provenance that misattributes is worse
                // than none: it invents evidence against cases that were clean.
                let divergencesByBuild (attemptNo: int) (rs: Receipt list) =
                    rs
                    |> List.mapi (fun bi (r: Receipt) ->
                        match r.Verdict with
                        | Diverged ds -> bi, (ds |> List.map (fun d -> d.Describe), attemptNo)
                        | _ -> bi, ([], attemptNo))
                    |> List.filter (fun (_, (ds, _)) -> not (List.isEmpty ds))
                    |> Map.ofList

                // Two extra attempts. This does NOT prove a surviving divergence is
                // real — it reduces a p-per-run flake to p^3, so the measured 2-8%
                // race becomes ~0.05% while a 50%-intermittent defect still passes
                // 12.5% of the time. After three attempts the case is TREATED as
                // real and fails closed, which is a decision rule, not a proof.
                // (An earlier draft of this comment said "is not the trace race",
                // which claimed more than the code delivers.)
                let rec confirm attemptNo (firstSeen: Map<int, string list * int> option) =
                    let rs = buildReceipts ()

                    if anyDiverged rs && attemptNo < 2 then
                        // Announced, not silent: a re-run that leaves no trace makes a
                        // sealed receipt indistinguishable from a first-run pass, and it
                        // is also the only evidence that this retry loop RAN at all.
                        // attemptNo counts COMPLETED attempts, so the one about to run is +2.
                        // Printing +1 renamed the attempt that just failed, which
                        // defeats using this line to audit that the loop ran 3 times.
                        printfn "  %-46s re-running (diverged, attempt %d of 3)" name (attemptNo + 2)

                        // Accumulated across attempts, first-seen winning PER BUILD.
                        // Freezing the whole map on the first diverging attempt lost a
                        // build that diverged only on attempt 2, and its receipt then
                        // sealed as an ordinary first-pass proof — the exact durable
                        // provenance this block exists to guarantee.
                        // attemptNo counts COMPLETED attempts, so this run was +1.
                        let thisAttempt = divergencesByBuild (attemptNo + 1) rs

                        let seen =
                            match firstSeen with
                            | None -> Some thisAttempt
                            | Some f ->
                                Some(
                                    thisAttempt
                                    |> Map.fold (fun acc bi ds -> if Map.containsKey bi acc then acc else Map.add bi ds acc) f
                                )

                        confirm (attemptNo + 1) seen
                    else
                        rs, firstSeen, attemptNo

                let results, firstSeen, retries = confirm 0 None

                // RECOVERED requires THIS build's re-run to actually PROVE it. "Not
                // diverged" also admits NotComparable, and calling a non-comparable
                // re-run "clean after 1 re-run" would be a false claim in the one place
                // a reader looks to find out whether the evidence is sound.
                //
                // Decided PER RECEIPT. Gating on the whole case being proven cleared the
                // map for every build when any sibling was still Diverged, so a build
                // that diverged on attempt 1 and was proven on attempt 3 sealed as an
                // ordinary first-attempt proof — destroying the per-build provenance
                // added in the same round. Two of my own fixes cancelling each other.
                let seenMap = defaultArg firstSeen Map.empty

                let recovered =
                    if retries = 0 then
                        Map.empty
                    else
                        results
                        |> List.mapi (fun bi (r: Receipt) ->
                            match r.Verdict, Map.tryFind bi seenMap with
                            | Proven, Some entry -> Some(bi, entry)
                            | _ -> None)
                        |> List.choose id
                        |> Map.ofList

                // Stamped into the RECEIPT, not just the console: the receipt is what
                // gets committed and read later, and without this a re-run receipt is
                // byte-identical to a first-attempt pass. Per build, so only the
                // receipt that actually diverged carries the claim.
                results
                |> List.mapi (fun bi r ->
                    let mine, firstAt =
                        match Map.tryFind bi recovered with
                        | Some(ds, at) -> ds, at
                        | None -> [], 0

                    if not (List.isEmpty mine) then
                        // The attempt THIS BUILD first diverged on, not the case's retry
                        // count: in a sequence where build 1 diverges on attempt 1 and
                        // build 2 first diverges on attempt 2, reporting both as a
                        // "first run" divergence with the same re-run count is false
                        // about the second.
                        recoveredCases.Add(caseNameFor bi, mine, firstAt, retries + 1) |> ignore

                    { r with RecoveredFrom = mine })
                |> List.mapi (fun bi r ->
                    let caseName = caseNameFor bi
                    let path = Compare.seal receiptDir r

                    if not (sealedPaths.Add path) then
                        failwith
                            $"receipt path collision: {path} was already sealed this run — a sequence's synthesized .b<N> name and a real case file resolve to the same receipt"

                    let workspaceCompared =
                        match r.Jenkins, r.Fogell with
                        | Some j, Some f -> Compare.workspaceWasCompared j f
                        | _ -> false

                    let verdict =
                        match r.Verdict with
                        // Keyed on THIS receipt's stamp, not the sequence's retry count:
                        // a sibling build that never diverged must not be labelled
                        // recovered. Same mistake as the receipt stamp one round earlier,
                        // fixed there and left here.
                        | Proven when workspaceCompared && not (List.isEmpty r.RecoveredFrom) -> "PROVEN(recovered)"
                        | Proven when workspaceCompared -> "PROVEN"
                        | Proven -> "PROVEN-PARTIAL"
                        | Diverged ds -> $"DIVERGED({ds.Length})"
                        | NotComparable _ -> "NOT-COMPARABLE"

                    printfn
                        "  %-46s %-16s %s"
                        (caseName.Substring(0, min 44 caseName.Length))
                        verdict
                        (Path.GetFileName path)

                    match r.Verdict with
                    | Diverged ds -> for d in ds do printfn "        %s" d.Describe
                    | NotComparable d -> printfn "        %s" d.Describe
                    | Proven -> ()

                    r))

        let isFull (r: Receipt) =
            r.Verdict = Proven
            && (match r.Jenkins, r.Fogell with
                | Some j, Some f -> Compare.workspaceWasCompared j f
                | _ -> false)

        let full = receipts |> List.filter isFull |> List.length
        let partial = receipts |> List.filter (fun r -> r.Verdict = Proven && not (isFull r)) |> List.length

        printfn ""
        if recoveredCases.Count > 0 then
            printfn ""
            printfn "RECOVERED — diverged, then did not reproduce on re-run (FG-119 retry)."
            printfn "  CAUSE UNCLASSIFIED: the retry covers every divergence, not just the"
            printfn "  known trace race. The SAME case recovering repeatedly is a defect report:"

            for (nm, seen, firstAt, total) in recoveredCases do
                printfn
                    "  %s — first diverged on attempt %d of %d, proven on the last; that attempt showed:"
                    nm
                    firstAt
                    total
                for d in seen do printfn "        %s" d

            printfn ""

        printfn "tier-1 proven (incl. workspace): %d / %d" full receipts.Length
        printfn "proven-partial (result+output):  %d / %d" partial receipts.Length

        // A configured evidence export is a REQUIRED observation, not a hint.
        // A safe but stale job/build selector used to do nothing and could leave
        // an older target file looking current even when the comparison passed.
        // Refuse the whole run unless the exact selected console was published.
        match rawConsoleExport with
        | Some configured when not configured.Observed ->
            eprintfn
                $"raw-console export refused: selected build was not observed: {configured.JobName} #{configured.BuildNumber}"

            2
        | _ ->
            // Exit non-zero unless every file is fully proven. A partial pass is not
            // a pass: it is a claim with a hole in it.
            if full = receipts.Length then 0 else 1
    | _ ->
        eprintfn "usage: fogell-diff <jenkins-url> <jenkins-core> <receipt-dir> <file...>"
        2
