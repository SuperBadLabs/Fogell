namespace Fogell.Differential

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

/// The Jenkins side of the differential. Drives a PINNED Jenkins over its REST
/// API, runs one Jenkinsfile, and reduces the run to a [Trace].
///
/// Pinning matters: a compatibility claim is against a specific Jenkins, not
/// "Jenkins" in the abstract. The image digest and core version are recorded in
/// every receipt.
type JenkinsConfig =
    { BaseUrl: string
      /// Recorded in the receipt so the claim names what it was measured against.
      CoreVersion: string
      /// A directory visible to this process, when Jenkins shares a filesystem.
      WorkspaceRoot: string option
      /// FG-002b. Jenkins usually does NOT share a filesystem — it runs in a
      /// container, often on another host. Rather than give up on comparing
      /// workspaces (which would cap every receipt at PROVEN-PARTIAL forever),
      /// the harness can be handed a command that hashes the workspace WHERE IT
      /// LIVES and prints the strict version-2 file/empty-leaf manifest consumed
      /// by `Trace.collectRemote`.
      ///
      /// `{job}` is substituted with the job name. Example:
      /// The committed runner is the executable reference emitter; `{job}` is
      /// substituted with the job name before invocation.
      ///
      /// The output is normalised through exactly the same exclusion rules as a
      /// local hash, so neither side gets a different definition of "workspace".
      WorkspaceCollector: string option
      /// Optional, exact build-scoped raw-console export. The console is written
      /// atomically after Jenkins returns it and before the disposable job is
      /// deleted. A configured write failure fails that build rather than leaving
      /// a stale or partial evidence artifact.
      RawConsoleExport: RawConsoleExport option
      /// FG-053. Whether the SCRIPT declares `options { timestamps() }`.
      ///
      /// Jenkins cannot be asked, and its console cannot be inspected for it
      /// without circularity — deciding to strip prefixes because prefixes are
      /// present is not a test. The side that PARSES knows, the same reasoning
      /// [Trace.Concurrent] already uses, so the CLI reads it off the script and
      /// tells both engines.
      DeclaresTimestamps: bool }

and RawConsoleExport =
    { JobName: string
      BuildNumber: int
      Path: string
      /// Set only after the selected console has been atomically published.
      /// The CLI checks this after every requested case, so a selector that
      /// matched no executed build cannot silently report success.
      mutable Observed: bool }

/// FG-052. What defines a build's pipeline on the Jenkins side: an inline
/// script (CpsFlowDefinition) or an SCM the Jenkinsfile is obtained from
/// (CpsScmFlowDefinition — `checkout scm` has meaning only here).
type JobDefinition =
    | Inline of script: string
    | FromScm of ScmSpec

module Jenkins =

    let private client = new HttpClient(Timeout = TimeSpan.FromMinutes 10.0)

    let jobNameForCase (casePath: string) =
        "diff-"
        + Regex.Replace(
            IO.Path.GetFileNameWithoutExtension(IO.Path.GetFileName casePath),
            "[^A-Za-z0-9]+",
            "-"
        )

    /// Jenkins execution and raw-console selection are keyed by job/build.
    /// Refuse two source cases that normalize to the same job before either
    /// case can execute.
    let validateUniqueCaseJobs (casePaths: string list) =
        let collisions =
            casePaths
            |> List.groupBy jobNameForCase
            |> List.choose (fun (job, paths) ->
                if List.length paths > 1 then Some(job, paths |> List.map IO.Path.GetFileName) else None)

        match collisions with
        | [] -> Ok()
        | _ ->
            collisions
            |> List.map (fun (job, names) -> sprintf "%s <- %s" job (String.concat ", " names))
            |> String.concat "; "
            |> sprintf "normalized Jenkins job-name collision: %s"
            |> Error

    let private hasReparsePoint (path: string) =
        let info = IO.FileInfo path

        info.LinkTarget <> null
        || ((IO.File.Exists path || IO.Directory.Exists path)
            && info.Attributes.HasFlag IO.FileAttributes.ReparsePoint)

    /// Refuse every existing symlink/reparse point in the lexical target chain.
    /// Resolving the whole path first would hide the evidence-directory escape.
    let private hasReparseComponent (path: string) =
        let full = IO.Path.GetFullPath path
        let root = IO.Path.GetPathRoot full

        full.Substring(root.Length)
            .Split(
                [| IO.Path.DirectorySeparatorChar; IO.Path.AltDirectorySeparatorChar |],
                StringSplitOptions.RemoveEmptyEntries
            )
        |> Array.mapFold (fun current part ->
            let next = IO.Path.Combine(current, part)
            hasReparsePoint next, next) root
        |> fst
        |> Array.exists id

    let internal exportRawConsole
        (export: RawConsoleExport option)
        (jobName: string)
        (buildNumber: int)
        (console: string)
        =
        match export with
        | Some configured when configured.JobName = jobName && configured.BuildNumber = buildNumber ->
            let target = configured.Path
            let directory = IO.Path.GetDirectoryName target

            if not (IO.Path.IsPathFullyQualified target) || String.IsNullOrEmpty directory then
                invalidOp "configured raw-console export path must be absolute"

            if not (IO.Directory.Exists directory) then
                invalidOp $"configured raw-console export directory does not exist: {directory}"

            if hasReparseComponent target then
                invalidOp $"configured raw-console export path passes through a symlink or reparse point: {target}"

            if IO.Directory.Exists target then
                invalidOp $"configured raw-console export target is a directory: {target}"

            let temporary =
                IO.Path.Combine(
                    directory,
                    $".{IO.Path.GetFileName target}.{Guid.NewGuid():N}.tmp"
                )

            try
                IO.File.WriteAllText(temporary, console, UTF8Encoding(false))
                IO.File.Move(temporary, target, true)
                configured.Observed <- true
            finally
                if IO.File.Exists temporary then
                    IO.File.Delete temporary
        | _ -> ()

    /// FG-129. Jenkins has no structured "compiler refused" result, so the
    /// distinction is reduced from the controller-owned terminal result and raw
    /// console. All three guards are load-bearing: compiler-shaped text is
    /// script-writable after execution begins, and a retained workspace says
    /// nothing about whether this build executed.
    let classifyExecutionDisposition (terminal: string) (rawLines: string[]) =
        let compilerHead = "org.codehaus.groovy.control.MultipleCompilationErrorsException: startup failed:"

        let firstIndex predicate = rawLines |> Array.tryFindIndex predicate

        let compilerLine = firstIndex (fun line -> line.Trim() = compilerHead)

        let workflowLine =
            firstIndex (fun line ->
                Regex.IsMatch(
                    line.Trim(),
                    @"^WorkflowScript: [1-9][0-9]*: .+ @ line [1-9][0-9]*, column [1-9][0-9]*\.$"
                ))

        let summaryLine =
            firstIndex (fun line -> Regex.IsMatch(line.Trim(), @"^[1-9][0-9]* errors?$"))

        let orderedEnvelope =
            match compilerLine, workflowLine, summaryLine with
            | Some c, Some w, Some e -> c < w && w < e
            | _ -> false

        let pipelineStarted =
            rawLines |> Array.exists (fun line -> line.Contains("[Pipeline]", StringComparison.Ordinal))

        if terminal = "failure" && not pipelineStarted && orderedEnvelope then
            RefusedBeforeExecution
        else
            ExecutedOrRuntime

    /// Parse controller-owned git-plugin BuildData. Build output is script-
    /// writable and therefore cannot attest a checkout; this API action is the
    /// authoritative harness boundary.
    let parseBuildDataRevisions (body: string) : Result<string list, string> =
        try
            use document = JsonDocument.Parse body
            let mutable actions = Unchecked.defaultof<JsonElement>

            if
                not (document.RootElement.TryGetProperty("actions", &actions))
                || actions.ValueKind <> JsonValueKind.Array
            then
                Error "Jenkins build API has no actions array"
            else
                actions.EnumerateArray()
                |> Seq.choose (fun action ->
                    let mutable revision = Unchecked.defaultof<JsonElement>
                    let mutable sha = Unchecked.defaultof<JsonElement>

                    if
                        action.ValueKind = JsonValueKind.Object
                        && action.TryGetProperty("lastBuiltRevision", &revision)
                        && revision.ValueKind = JsonValueKind.Object
                        && revision.TryGetProperty("SHA1", &sha)
                        && sha.ValueKind = JsonValueKind.String
                    then
                        match sha.GetString() with
                        | value when not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{40}$") -> Some value
                        | _ -> None
                    else
                        None)
                |> Seq.distinct
                |> Seq.sort
                |> List.ofSeq
                |> Ok
        with ex ->
            Error $"invalid Jenkins BuildData JSON ({ex.Message})"

    /// The SCM-defined Jenkinsfile is loaded before user Pipeline code starts.
    /// In the evidence lane we force CpsScmFlowDefinition's full-checkout path,
    /// then read only the controller-written console prefix before the first
    /// `[Pipeline] Start of Pipeline`. A later `checkout scm` (or script output)
    /// therefore cannot overwrite or spoof this definition identity.
    let parseScmDefinitionRevision (console: string) : Result<string, string> =
        let lines = console.Replace("\r\n", "\n").Split '\n'

        match
            lines
            |> Array.tryFindIndex (fun line ->
                line.Trim().Contains("[Pipeline] Start of Pipeline", StringComparison.Ordinal))
        with
        | None -> Error "Jenkins console has no Pipeline start boundary for SCM definition attestation"
        | Some boundary ->
            let revisions =
                lines |> Array.take boundary
                |> Array.choose (fun line ->
                    let matched = Regex.Match(line.Trim(), @"^Checking out Revision ([0-9a-f]{40}) \(")
                    if matched.Success then Some matched.Groups[1].Value else None)
                |> Array.distinct
                |> Array.toList

            match revisions with
            | [ revision ] -> Ok revision
            | [] -> Error "Jenkins console has no pre-Pipeline SCM definition checkout revision"
            | _ ->
                let joined = String.concat "," revisions
                Error $"Jenkins console has multiple pre-Pipeline SCM definition revisions: {joined}"

    /// FG-129. A compiler-refused SCM definition has no Pipeline-start marker.
    /// The raw-console classifier is the authority that this is genuinely a
    /// pre-execution refusal; only then may the exact compiler head replace the
    /// Pipeline marker as the end of controller-owned definition-checkout text.
    let parseScmDefinitionRevisionFor (disposition: ExecutionDisposition) (console: string) =
        match disposition with
        | ExecutedOrRuntime -> parseScmDefinitionRevision console
        | RefusedBeforeExecution ->
            let lines = console.Replace("\r\n", "\n").Split '\n'
            let compilerHead = "org.codehaus.groovy.control.MultipleCompilationErrorsException: startup failed:"

            match lines |> Array.tryFindIndex (fun line -> line.Trim() = compilerHead) with
            | None -> Error "Jenkins refused disposition has no compiler boundary for SCM definition attestation"
            | Some boundary ->
                let revisions =
                    lines
                    |> Array.take boundary
                    |> Array.choose (fun line ->
                        let matched = Regex.Match(line.Trim(), @"^Checking out Revision ([0-9a-f]{40}) \(")
                        if matched.Success then Some matched.Groups[1].Value else None)
                    |> Array.distinct
                    |> Array.toList

                match revisions with
                | [ revision ] -> Ok revision
                | [] -> Error "Jenkins console has no pre-compiler SCM definition checkout revision"
                | _ ->
                    let joined = String.concat "," revisions
                    Error $"Jenkins console has multiple pre-compiler SCM definition revisions: {joined}"

    let private crumb (cfg: JenkinsConfig) =
        task {
            let! body = client.GetStringAsync $"{cfg.BaseUrl}/crumbIssuer/api/json"
            let field = Regex.Match(body, "\"crumbRequestField\":\"([^\"]+)\"").Groups[1].Value
            let value = Regex.Match(body, "\"crumb\":\"([^\"]+)\"").Groups[1].Value
            return field, value
        }
        |> fun t -> t.Result

    let private xmlEscape (s: string) =
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

    let private jobXml (script: string) =
        "<flow-definition plugin=\"workflow-job\"><description/><keepDependencies>false</keepDependencies>"
        + "<properties>"
        // PERFORMANCE_OPTIMIZED deliberately: the differential compares SEMANTICS,
        // and MAX_SURVIVABILITY costs ~6.9 fsyncs per step without changing any
        // observable output. Durability is compared separately, not here.
        + "<org.jenkinsci.plugins.workflow.job.properties.DurabilityHintJobProperty>"
        + "<hint>PERFORMANCE_OPTIMIZED</hint>"
        + "</org.jenkinsci.plugins.workflow.job.properties.DurabilityHintJobProperty>"
        + "</properties>"
        + "<definition class=\"org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition\" plugin=\"workflow-cps\">"
        + $"<script>{xmlEscape script}</script><sandbox>true</sandbox></definition>"
        + "<triggers/><disabled>false</disabled></flow-definition>"

    /// CpsScmFlowDefinition: the job POINTS AT the SCM; Jenkins obtains the
    /// Jenkinsfile from it (lightweight) and Declarative auto-checks-out.
    let scmJobXml (attestDefinition: bool) (spec: ScmSpec) =
        let lightweight = if attestDefinition then "false" else "true"
        "<flow-definition plugin=\"workflow-job\"><description/><keepDependencies>false</keepDependencies>"
        + "<properties>"
        + "<org.jenkinsci.plugins.workflow.job.properties.DurabilityHintJobProperty>"
        + "<hint>PERFORMANCE_OPTIMIZED</hint>"
        + "</org.jenkinsci.plugins.workflow.job.properties.DurabilityHintJobProperty>"
        + "</properties>"
        + "<definition class=\"org.jenkinsci.plugins.workflow.cps.CpsScmFlowDefinition\" plugin=\"workflow-cps\">"
        + "<scm class=\"hudson.plugins.git.GitSCM\" plugin=\"git\"><configVersion>2</configVersion>"
        + "<userRemoteConfigs><hudson.plugins.git.UserRemoteConfig>"
        + $"<url>{xmlEscape spec.Url}</url>"
        + "</hudson.plugins.git.UserRemoteConfig></userRemoteConfigs>"
        + "<branches><hudson.plugins.git.BranchSpec>"
        + $"<name>*/{xmlEscape spec.Branch}</name>"
        + "</hudson.plugins.git.BranchSpec></branches>"
        + "<doGenerateSubmoduleConfigurations>false</doGenerateSubmoduleConfigurations>"
        + "<submoduleCfg class=\"empty-list\"/><extensions/></scm>"
        + $"<scriptPath>Jenkinsfile</scriptPath><lightweight>{lightweight}</lightweight>"
        + "</definition><triggers/><disabled>false</disabled></flow-definition>"

    /// FG-110. Run a SEQUENCE of builds of ONE job and return a trace per
    /// build. The job is created once, its definition UPDATED between builds
    /// (a sequence's scripts may differ), and deleted only at the end, so
    /// build history exists and `changed`/`fixed`/`regression` can select
    /// against a real previous result. Each build is polled BY NUMBER — build
    /// k of the sequence is build #k of the job — and the workspace is hashed
    /// after each build, exactly where it lives.
    let runMany
        (cfg: JenkinsConfig)
        (envReplacements: (string * string) list)
        (jobName: string)
        (builds: JobDefinition list)
        : Result<Trace, string> list =
        try
            let field, value = crumb cfg

            let post (path: string) (content: HttpContent option) =
                let req = new HttpRequestMessage(HttpMethod.Post, $"{cfg.BaseUrl}{path}")
                req.Headers.Add(field, value)
                content |> Option.iter (fun c -> req.Content <- c)
                let r = client.Send req
                int r.StatusCode

            post $"/job/{jobName}/doDelete" None |> ignore

            let runOneInner (buildNumber: int) (definition: JobDefinition) : Result<Trace, string> =
                let xml () =
                    let body =
                        match definition with
                        | Inline script -> jobXml script
                        | FromScm spec ->
                            scmJobXml
                                (Environment.GetEnvironmentVariable "FOGELL_SCM_ATTESTATION" = "fg177-probes-v1")
                                spec

                    new StringContent(body, Encoding.UTF8, "application/xml")

                let ready =
                    if buildNumber = 1 then
                        let created = post $"/createItem?name={jobName}" (Some(xml ()))

                        if created = 200 || created = 201 then
                            Ok()
                        else
                            Error $"createItem returned HTTP {created}"
                    else
                        // update the definition in place; history survives
                        let updated = post $"/job/{jobName}/config.xml" (Some(xml ()))
                        if updated = 200 then Ok() else Error $"config.xml update returned HTTP {updated}"

                match ready with
                | Error e -> Error e
                | Ok() ->

                // FG-103: the trigger's status propagates — a stale crumb or a 409
                // otherwise means five minutes of blind polling for a build that
                // never exists, blamed on "did not reach a terminal state".
                match post $"/job/{jobName}/build" None with
                | 200
                | 201 -> ()
                | other -> failwith $"build trigger returned HTTP {other}"

                // poll THIS build number to a terminal state
                let mutable result = None
                let mutable attempts = 0

                while result.IsNone && attempts < 600 do
                    Threading.Thread.Sleep 500
                    attempts <- attempts + 1

                    try
                        let body =
                            client.GetStringAsync($"{cfg.BaseUrl}/job/{jobName}/{buildNumber}/api/json").Result

                        if Regex.IsMatch(body, "\"building\":false") then
                            let m = Regex.Match(body, "\"result\":\"([A-Z_]+)\"")
                            if m.Success then result <- Some(m.Groups[1].Value.ToLowerInvariant())
                    with _ ->
                        ()

                match result with
                | None -> Error "jenkins build did not reach a terminal state"
                | Some terminal ->
                    let console =
                        client.GetStringAsync($"{cfg.BaseUrl}/job/{jobName}/{buildNumber}/consoleText").Result

                    exportRawConsole cfg.RawConsoleExport jobName buildNumber console

                    let rawLines = console.Replace("\r\n", "\n").Split '\n'
                    let disposition = classifyExecutionDisposition terminal rawLines

                    let scmEngineNotes =
                        if Environment.GetEnvironmentVariable "FOGELL_SCM_ATTESTATION" = "fg177-probes-v1" then
                            let definitionNotes =
                                match definition with
                                | Inline _ -> []
                                | FromScm _ ->
                                    match parseScmDefinitionRevisionFor disposition console with
                                    | Ok revision -> [ $"scm-definition revision={revision}" ]
                                    | Error e -> failwith $"SCM definition attestation unavailable ({e})"

                            let tree = Uri.EscapeDataString "actions[lastBuiltRevision[SHA1]]"
                            let buildData =
                                client.GetStringAsync(
                                    $"{cfg.BaseUrl}/job/{jobName}/{buildNumber}/api/json?tree={tree}"
                                ).Result

                            match parseBuildDataRevisions buildData with
                            | Ok revisions ->
                                definitionNotes
                                @ (revisions |> List.map (fun revision -> $"git-build-data revision={revision}"))
                            | Error e -> failwith $"SCM attestation unavailable ({e})"
                        else
                            []

                    let workspaceHash, files =
                        match cfg.WorkspaceRoot, cfg.WorkspaceCollector with
                        | Some root, _ -> Trace.hashWorkspace (IO.Path.Combine(root, jobName))
                        | None, Some template -> Trace.collectRemote (template.Replace("{job}", jobName))
                        | None, None -> "not-collected", []

                    let declaresTimestamps = cfg.DeclaresTimestamps

                    // hoisted so the timestamp coverage can use the SAME list the
                    // comparison uses as its denominator
                    let outputLines, timestampCounts =
                        let fromBanner =
                            rawLines
                            |> Array.tryPick (fun l ->
                                let m = Text.RegularExpressions.Regex.Match(l.Trim(), "^Running on .+ in (/.+)$")
                                if m.Success then Some m.Groups[1].Value else None)

                        let ws = defaultArg fromBanner $"/var/jenkins_home/workspace/{jobName}"
                        Trace.normaliseOutputShapedWithTimestampCoverage
                            declaresTimestamps
                            true
                            [ ws, "${WORKSPACE}" ]
                            envReplacements
                            rawLines

                    let trace =
                        { Disposition = disposition
                          Result = terminal
                          EngineNotes = scmEngineNotes
                          // the workspace root is READ from the run's own banner —
                          // `Running on <node> in <path>` — so a non-default
                          // JENKINS_HOME or a remote agent canonicalises correctly;
                          // the pinned controller path is only the fallback
                          Output = outputLines
                          WorkspaceHash = workspaceHash
                          WorkspaceFiles = files
                          // Jenkins does not tell us whether the script had a
                          // parallel block; the side that parses does.
                          Concurrent = false
                          // FG-118: the counts come from the same tagged survivor
                          // list as Output. A stamped annotation can no longer
                          // offset an unstamped line that is actually compared.
                          Timestamps = timestampCounts
                          ReportedFailureReason = Trace.reportedFailureReasonWhen declaresTimestamps rawLines }

                    Ok trace

            // TOTAL per build: a throw while collecting build k (console fetch,
            // remote workspace collector) is build k's OWN error — it must not
            // reach the outer handler and replace builds 1..k-1's already-collected
            // evidence with a misattributed message.
            let runOne (buildNumber: int) (definition: JobDefinition) : Result<Trace, string> =
                try
                    runOneInner buildNumber definition
                with ex ->
                    Error ex.Message

            let results =
                builds
                |> List.fold
                    (fun (acc, halted) definition ->
                        match halted with
                        | Some why -> (Error $"sequence halted: {why}" :: acc, halted)
                        | None ->
                            let r = runOne (List.length acc + 1) definition

                            match r with
                            | Ok _ -> (r :: acc, None)
                            | Error why -> (r :: acc, Some $"a prior build failed to run ({why})"))
                    ([], None)
                |> fun (acc, _) -> List.rev acc

            // Best-effort cleanup AFTER the evidence is safe: a delete failure
            // must not replace collected traces (the next run of this case
            // deletes the job first anyway).
            (try
                post $"/job/{jobName}/doDelete" None |> ignore
             with _ ->
                 ())

            results
        with ex ->
            // one entry PER REQUESTED BUILD, so a caller zipping against the
            // fogell side cannot misalign a sequence on a harness exception
            builds |> List.map (fun _ -> Error ex.Message)

    /// Run one Jenkinsfile under a disposable job name — the pre-FG-110 contract.
    let run (cfg: JenkinsConfig) (envReplacements: (string * string) list) (jobName: string) (script: string) =
        match runMany cfg envReplacements jobName [ Inline script ] with
        | [ r ] -> r
        | _ -> Error "single-build run returned an unexpected shape"
