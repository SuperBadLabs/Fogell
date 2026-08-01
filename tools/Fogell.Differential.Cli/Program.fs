module Fogell.Differential.Cli.Program

open System
open System.IO
open Fogell.Differential

/// FG-002. Runs one or more Jenkinsfiles through BOTH engines and seals a
/// receipt per file.
///
/// Usage:
///   fogell-diff <jenkins-url> <jenkins-core> <receipt-dir> <file.Jenkinsfile>...
///
/// Environment:
///   FOGELL_JENKINS_WORKSPACE  host path of Jenkins' workspace root (optional;
///                             without it, workspace hashes are not compared)
[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | baseUrl :: core :: receiptDir :: (_ :: _ as files) ->
        let jenkinsWorkspace =
            match Environment.GetEnvironmentVariable "FOGELL_JENKINS_WORKSPACE" with
            | null | "" -> None
            | v -> Some v

        let collector =
            match Environment.GetEnvironmentVariable "FOGELL_JENKINS_WORKSPACE_CMD" with
            | null | "" -> None
            | v -> Some v

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
        let envReplacements =
            if not envCanonicalisationEnabled then
                []
            else
                Fogell.Differential.Trace.canonicalisedEnvNames
                |> List.collect (fun name ->
                    [ match jenkinsEnv |> List.tryFind (fun (n, _) -> n = name) with
                      | Some(_, v) when v <> "" -> yield v, "${" + name + "}"
                      | _ -> ()

                      match Environment.GetEnvironmentVariable name with
                      | null
                      | "" -> ()
                      | v -> yield v, "${" + name + "}" ])
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
              WorkspaceCollector = collector }

        let fogellRoot =
            Path.Combine(Path.GetTempPath(), "fogell-diff-" + Guid.NewGuid().ToString("N").Substring(0, 8))

        Directory.CreateDirectory fogellRoot |> ignore

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

        let receipts =
            files
            |> List.collect (fun file ->
                let name = Path.GetFileName file
                let script = File.ReadAllText file
                // Job names MUST be derived from the file, not its index: an
                // index-derived name reshuffles when a case is added, and the
                // new occupant inherits the previous run's workspace. That
                // showed up as a phantom workspace-hash divergence.
                let job =
                    "diff-"
                    + Text.RegularExpressions.Regex.Replace(
                        Path.GetFileNameWithoutExtension name, "[^A-Za-z0-9]+", "-")

                // wipe any stale workspace so a hash can only match on merit
                match Environment.GetEnvironmentVariable "FOGELL_JENKINS_WIPE_CMD" with
                | null
                | "" -> ()
                | template ->
                    let psi = Diagnostics.ProcessStartInfo("/bin/sh")
                    psi.ArgumentList.Add "-c"
                    psi.ArgumentList.Add(template.Replace("{job}", job))
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError <- true
                    use p = Diagnostics.Process.Start psi
                    p.WaitForExit 30_000 |> ignore

                // FG-052. A case whose FIRST LINE is `//// SCM JOB ////` runs as an
                // SCM-DEFINED job: the sync script pushed its body to the fixture
                // repo branch `case/<stem>` (scripts/sync-scm-cases.bb), the Jenkins
                // job points at that SCM, and the Fogell side receives the same
                // bytes plus the ScmSpec `checkout scm` resolves against.
                let scmMarker = "//// SCM JOB ////"
                let isScmCase = script.StartsWith scmMarker

                let scripts =
                    if isScmCase then
                        [ script.Substring(script.IndexOf '\n' + 1) ]
                    else
                        buildSeparator.Split script |> Array.toList

                // A malformed sequence (leading/trailing/doubled separator) must
                // fail NAMED, not as an opaque parse error on an empty pipeline.
                let malformed =
                    List.length scripts > 1 && scripts |> List.exists String.IsNullOrWhiteSpace

                let scmSpec =
                    if isScmCase then
                        let url =
                            match Environment.GetEnvironmentVariable "FOGELL_SCM_URL" with
                            | null
                            | "" -> "git://100.105.179.51/repo.git"
                            | u -> u

                        Some
                            { Url = url
                              Branch = $"case/{Path.GetFileNameWithoutExtension name}" }
                    else
                        None

                let jenkinsRuns, fogellRuns =
                    if malformed then
                        let e = Result.Error "malformed sequence file: empty build segment around a //// NEXT BUILD //// separator"
                        scripts |> List.map (fun _ -> e), scripts |> List.map (fun _ -> e)
                    else
                        match scmSpec with
                        | Some spec ->
                            Jenkins.runMany cfg envReplacementsAll job [ FromScm spec ],
                            [ FogellSide.runScm envReplacementsAll fogellRoot job spec scripts.Head ]
                        | None ->
                            Jenkins.runMany cfg envReplacementsAll job (scripts |> List.map Inline),
                            FogellSide.runMany envReplacementsAll fogellRoot job scripts

                let solo = List.length scripts = 1

                List.zip jenkinsRuns fogellRuns
                |> List.mapi (fun bi (jenkins, fogell) ->
                    let caseName =
                        if solo then
                            name
                        else
                            // suffix inserted before the FINAL extension (never
                            // string-Replace, which rewrites every occurrence and
                            // no-ops on other extensions, sealing N builds over
                            // one receipt)
                            let stem = Path.GetFileNameWithoutExtension name
                            let ext = Path.GetExtension name
                            $"{stem}.b{bi + 1}{ext}"

                    let r = Compare.receipt caseName core envReplacementsAll jenkins fogell
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
        printfn "tier-1 proven (incl. workspace): %d / %d" full receipts.Length
        printfn "proven-partial (result+output):  %d / %d" partial receipts.Length

        // Exit non-zero unless every file is fully proven. A partial pass is not
        // a pass: it is a claim with a hole in it.
        if full = receipts.Length then 0 else 1
    | _ ->
        eprintfn "usage: fogell-diff <jenkins-url> <jenkins-core> <receipt-dir> <file...>"
        2
