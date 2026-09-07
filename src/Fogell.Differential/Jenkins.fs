namespace Fogell.Differential

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
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
      /// Optional exact PATH injected as a Jenkins string parameter on every
      /// disposable build. Runtime-pinned corpus cases use this to bind command
      /// resolution in the real Jenkins `sh` launcher rather than merely in an
      /// out-of-band `podman exec` inspection.
      BuildPath: string option
      /// Optional build-context guard for a runtime-pinned corpus case. The
      /// guard is executed as real Pipeline `sh` builds immediately before and
      /// after the requested build on a separate disposable Jenkins job, so it
      /// cannot manufacture history for the tested job. Every allocation,
      /// including the corpus build itself, must name RequiredNode.
      RuntimeGuard: RuntimeGuard option
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

and RuntimeGuard =
    { CaseSha: string
      RequiredNode: string
      BuildPath: string
      Requirements: RuntimeRequirement list }

and RuntimeRequirement =
    | PresentAtPath of command: string * path: string
    | AbsentCommand of command: string

/// FG-052. What defines a build's pipeline on the Jenkins side: an inline
/// script (CpsFlowDefinition) or an SCM the Jenkinsfile is obtained from
/// (CpsScmFlowDefinition — `checkout scm` has meaning only here).
type JobDefinition =
    | Inline of script: string
    | FromScm of ScmSpec

module Jenkins =

    let private client = new HttpClient(Timeout = TimeSpan.FromMinutes 10.0)

    /// Translate the corpus lane's deliberately small environment protocol into
    /// a typed runtime requirement. The wire format carries a path in every mode
    /// so partial old/new runner combinations fail closed: present requires one
    /// absolute safe path, while absent requires the exact non-path sentinel `-`.
    let configureRuntimeGuard
        (caseSha: string)
        (node: string)
        (command: string)
        (toolPath: string)
        (expectation: string)
        (buildPath: string option)
        (caseCount: int)
        =
        let values = [ caseSha; node; command; toolPath; expectation ]
        let present value = not (String.IsNullOrEmpty value)
        let safe (pattern: string) (value: string) = Regex.IsMatch(value, pattern)

        match values |> List.filter present |> List.length with
        | 0 -> Ok(None, None)
        | 5 ->
            let commonValid =
                safe "^[0-9a-f]{64}$" caseSha
                && safe "^[A-Za-z0-9._-]+$" node
                && safe "^[A-Za-z0-9._+-]+$" command

            let requirement =
                match expectation with
                | "present" when safe "^/[A-Za-z0-9._/+:-]+$" toolPath ->
                    Ok(PresentAtPath(command, toolPath))
                | "present" -> Error "expectation 'present' requires an absolute safe tool path"
                | "absent" when toolPath = "-" -> Ok(AbsentCommand command)
                | "absent" -> Error "expectation 'absent' requires tool path '-'"
                | _ -> Error "expectation must be exactly 'present' or 'absent'"

            match commonValid, requirement, buildPath, caseCount with
            | false, _, _, _ -> Error "malformed case SHA, node, or command"
            | true, Error why, _, _ -> Error why
            | true, Ok requirement, Some path, 1 ->
                Ok(
                    Some caseSha,
                    Some
                        { CaseSha = caseSha
                          RequiredNode = node
                          BuildPath = path
                          Requirements = [ requirement ] }
                )
            | true, Ok _, Some _, _ -> Error "exactly one corpus case is required"
            | true, Ok _, None, _ -> Error "FOGELL_JENKINS_BUILD_PATH is required"
        | _ -> Error "all five FOGELL_RUNTIME_GUARD_* values are required"

    let jobNameForCase (casePath: string) =
        "diff-"
        + Regex.Replace(
            IO.Path.GetFileNameWithoutExtension(IO.Path.GetFileName casePath),
            "[^A-Za-z0-9]+",
            "-"
        )

    /// jobNameForCase always starts with `diff-` and cannot emit `_`; this
    /// reserved sibling namespace cannot collide with an ordinary case job.
    let internal runtimeGuardJobName (corpusJobName: string) =
        $"_fogell-runtime-guard-{corpusJobName}"

    let internal cleanupJobNames (corpusJobName: string) (hasRuntimeGuard: bool) =
        if hasRuntimeGuard then
            [ corpusJobName; runtimeGuardJobName corpusJobName ]
        else
            [ corpusJobName ]

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

    let private buildPathProperty (buildPath: string option) =
        match buildPath with
        | None -> ""
        | Some path ->
            "<hudson.model.ParametersDefinitionProperty><parameterDefinitions>"
            + "<hudson.model.StringParameterDefinition><name>PATH</name>"
            + "<description>Fogell exact build command-resolution path</description>"
            + $"<defaultValue>{xmlEscape path}</defaultValue><trim>false</trim>"
            + "</hudson.model.StringParameterDefinition>"
            + "<hudson.model.StringParameterDefinition><name>FOGELL_BUILD_TOKEN</name>"
            + "<description>Per-trigger Fogell queue ownership token</description>"
            + "<defaultValue></defaultValue><trim>false</trim>"
            + "</hudson.model.StringParameterDefinition></parameterDefinitions>"
            + "</hudson.model.ParametersDefinitionProperty>"

    let internal buildTriggerPath
        (jobName: string)
        (buildPath: string option)
        (buildToken: string option)
        =
        match buildPath, buildToken with
        | None, None -> $"/job/{jobName}/build"
        | Some path, token ->
            let pathPart = $"PATH={Uri.EscapeDataString path}"
            let tokenPart =
                token
                |> Option.map (fun value -> $"&FOGELL_BUILD_TOKEN={Uri.EscapeDataString value}")
                |> Option.defaultValue ""
            $"/job/{jobName}/buildWithParameters?{pathPart}{tokenPart}"
        | None, Some _ -> invalidArg (nameof buildToken) "a build ownership token requires a PATH parameterized job"

    let internal runtimeGuardScript (guard: RuntimeGuard) =
        let checks =
            guard.Requirements
            |> List.map (function
                | PresentAtPath(command, path) ->
                    $"actual=$(command -v {command}) || exit 91\n"
                    + $"[ \"$actual\" = \"{path}\" ] || exit 92\n"
                | AbsentCommand command ->
                    $"if command -v {command} >/dev/null 2>&1; then exit 94; fi\n")
            |> String.concat ""

        "pipeline {\n"
        + "  agent any\n"
        + "  stages {\n"
        + "    stage('Fogell runtime guard') {\n"
        + "      steps {\n"
        + "        sh '''set +x\n"
        + $"[ \"$PATH\" = \"{guard.BuildPath}\" ] || exit 90\n"
        + checks
        + "printf 'FOGELL_RUNTIME_GUARD_OK\\n'\n"
        + "'''\n"
        + "      }\n"
        + "    }\n"
        + "  }\n"
        + "}\n"

    let internal targetRuntimeMarker (caseSha: string) (nonce: string) =
        $"FOGELL_TARGET_RUNTIME_OK:{caseSha}:{nonce}"

    let internal injectTargetRuntimeGuard
        (guard: RuntimeGuard)
        (buildToken: string)
        (marker: string)
        (script: string)
        =
        // Parse the insertion point with the same Declarative grammar that
        // admitted the source. The first runtime-backed corpus case happened
        // to use two-space indentation, and the original exact-string anchor
        // silently turned that author's formatting into an execution
        // prerequisite. FG-257's next case uses four spaces and exposed the
        // false refusal before Jenkins ran it. Parser ownership also prevents
        // a comment, string, or nested `stages` spelling from becoming an
        // accidental injection point.
        match Fogell.Pipeline.Parser.Parser.topLevelStagesBodyStart script with
        | Error refusal ->
            Error $"runtime-guarded corpus definition has no parsed top-level stages insertion point: {refusal}"
        | Ok bodyStart ->
            let checks =
                guard.Requirements
                |> List.map (function
                    | PresentAtPath(command, path) ->
                        $"          actual=$(command -v {command}) || exit 91\n"
                        + $"          [ \"$actual\" = \"{path}\" ] || exit 92\n"
                    | AbsentCommand command ->
                        $"          if command -v {command} >/dev/null 2>&1; then exit 94; fi\n")
                |> String.concat ""

            let stage =
                "\n    stage('Fogell target runtime guard') {\n"
                + "      steps {\n"
                + "        sh '''#!/bin/sh\n"
                + "          set +x\n"
                + $"          [ \"$PATH\" = \"{guard.BuildPath}\" ] || exit 90\n"
                + $"          [ \"$FOGELL_BUILD_TOKEN\" = \"{buildToken}\" ] || exit 93\n"
                + checks
                + "          printf '%s\\n' '" + marker + "'\n"
                + "        '''\n"
                + "      }\n"
                + "    }\n"

            Ok(script.Insert(bodyStart, stage))

    let internal validateAndRemoveTargetRuntimeMarker
        (declaresTimestamps: bool)
        (marker: string)
        (rawLines: string array)
        =
        let matches line =
            String.Equals(
                (Trace.stripDecoration declaresTimestamps line).Trim(),
                marker,
                StringComparison.Ordinal
            )

        match rawLines |> Array.filter matches |> Array.length with
        | 1 -> Ok(rawLines |> Array.filter (matches >> not))
        | count -> Error $"target runtime guard emitted {count} exact success markers, expected one"

    let internal validateRuntimeGuardNode (requiredNode: string) (rawLines: string array) =
        let allocations =
            rawLines
            |> Array.choose (fun line ->
                let m = Regex.Match(line.Trim(), "^Running on (.+) in /.+$")
                if m.Success then Some m.Groups[1].Value else None)
            |> Array.distinct

        match allocations with
        | [| node |] when node = requiredNode -> Ok()
        | [| node |] -> Error $"runtime guard required Jenkins node '{requiredNode}', build ran on '{node}'"
        | _ -> Error "runtime guard could not bind the Jenkins build to one reported node"

    let internal replayMainScript (html: string) =
        let matches =
            Regex.Matches(
                html,
                "<textarea[^>]*name=\"_.mainScript\"[^>]*>(.*?)</textarea>",
                RegexOptions.Singleline
            )

        match matches.Count with
        | 1 -> Ok(WebUtility.HtmlDecode matches[0].Groups[1].Value)
        | count -> Error $"executed Jenkins definition had {count} Replay script fields, expected one"

    let internal validateBuildPathParameter (expectedPath: string) (json: string) =
        try
            use document = JsonDocument.Parse json

            let values =
                document.RootElement.GetProperty("actions").EnumerateArray()
                |> Seq.collect (fun action ->
                    let mutable parameters = Unchecked.defaultof<JsonElement>
                    if action.TryGetProperty("parameters", &parameters)
                       && parameters.ValueKind = JsonValueKind.Array then
                        parameters.EnumerateArray() |> Seq.toArray
                    else
                        [||])
                |> Seq.choose (fun parameter ->
                    let mutable name = Unchecked.defaultof<JsonElement>
                    let mutable value = Unchecked.defaultof<JsonElement>
                    if parameter.TryGetProperty("name", &name)
                       && name.ValueKind = JsonValueKind.String
                       && name.GetString() = "PATH"
                       && parameter.TryGetProperty("value", &value)
                       && value.ValueKind = JsonValueKind.String then
                        Some(value.GetString())
                    else
                        None)
                |> Seq.toList

            match values with
            | [ path ] when path = expectedPath -> Ok()
            | [ path ] -> Error $"Jenkins build PATH parameter was '{path}', expected '{expectedPath}'"
            | _ -> Error "Jenkins build did not record exactly one PATH parameter"
        with ex ->
            Error $"Jenkins build PATH parameter evidence was malformed ({ex.Message})"

    let internal queueItemId (location: string) =
        let match' = Regex.Match(location, "/queue/item/([1-9][0-9]*)/?(?:$|[?#])")
        if match'.Success then Ok(Int64.Parse match'.Groups[1].Value)
        else Error "Jenkins trigger did not return one parseable queue-item Location"

    let internal queueExecutableNumber (json: string) =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement
            let mutable cancelled = Unchecked.defaultof<JsonElement>

            if root.TryGetProperty("cancelled", &cancelled)
               && cancelled.ValueKind = JsonValueKind.True then
                Error "owned Jenkins queue item was cancelled"
            else
                let mutable executable = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("executable", &executable)
                   && executable.ValueKind = JsonValueKind.Object then
                    let mutable number = Unchecked.defaultof<JsonElement>
                    if executable.TryGetProperty("number", &number)
                       && number.ValueKind = JsonValueKind.Number then
                        Ok(Some(number.GetInt32()))
                    else
                        Error "owned Jenkins queue executable had no numeric build identity"
                else
                    Ok None
        with ex ->
            Error $"owned Jenkins queue evidence was malformed ({ex.Message})"

    let internal validateBuildOwnership
        (expectedQueueId: int64)
        (expectedToken: string)
        (json: string)
        =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement
            let queueId = root.GetProperty("queueId").GetInt64()

            let tokens =
                root.GetProperty("actions").EnumerateArray()
                |> Seq.collect (fun action ->
                    let mutable parameters = Unchecked.defaultof<JsonElement>
                    if action.TryGetProperty("parameters", &parameters)
                       && parameters.ValueKind = JsonValueKind.Array then
                        parameters.EnumerateArray() |> Seq.toArray
                    else
                        [||])
                |> Seq.choose (fun parameter ->
                    let mutable name = Unchecked.defaultof<JsonElement>
                    let mutable value = Unchecked.defaultof<JsonElement>
                    if parameter.TryGetProperty("name", &name)
                       && name.ValueKind = JsonValueKind.String
                       && name.GetString() = "FOGELL_BUILD_TOKEN"
                       && parameter.TryGetProperty("value", &value)
                       && value.ValueKind = JsonValueKind.String then
                        Some(value.GetString())
                    else
                        None)
                |> Seq.toList

            match queueId, tokens with
            | observed, [ token ] when observed = expectedQueueId && token = expectedToken -> Ok()
            | observed, _ when observed <> expectedQueueId ->
                Error $"Jenkins build queueId was {observed}, expected owned queue item {expectedQueueId}"
            | _, [ token ] ->
                Error $"Jenkins build ownership token was '{token}', expected the per-trigger token"
            | _ -> Error "Jenkins build did not record exactly one ownership token"
        with ex ->
            Error $"Jenkins build ownership evidence was malformed ({ex.Message})"

    let internal runtimeGuardResultFailure (result: Result<Trace, string>) =
        match result with
        | Error why -> Some why
        | Ok trace when trace.Result <> "success" -> Some $"guard build ended {trace.Result}"
        | Ok trace when trace.Output |> List.filter ((=) "FOGELL_RUNTIME_GUARD_OK") |> List.length <> 1 ->
            Some "guard build did not emit exactly one success marker"
        | Ok _ -> None

    type internal ScheduledBuild =
        { IsGuard: bool
          JobName: string
          BuildNumber: int
          Definition: JobDefinition
          ExpectedTargetMarker: string option
          ExpectedBuildToken: string option }

    let internal scheduleBuilds
        (corpusJobName: string)
        (guardJobName: string)
        (guardDefinition: JobDefinition option)
        (targetMarker: string option)
        (buildTokenNonce: string option)
        (builds: JobDefinition list)
        =
        let token isGuard job number =
            buildTokenNonce
            |> Option.map (fun nonce ->
                if isGuard then $"fogell:{nonce}:guard:{job}:{number}"
                else nonce)

        let corpus =
            builds
            |> List.mapi (fun index definition ->
                { IsGuard = false
                  JobName = corpusJobName
                  BuildNumber = index + 1
                  Definition = definition
                  ExpectedTargetMarker = targetMarker
                  ExpectedBuildToken = token false corpusJobName (index + 1) })

        match guardDefinition with
        | None -> corpus
        | Some probe ->
            { IsGuard = true
              JobName = guardJobName
              BuildNumber = 1
              Definition = probe
              ExpectedTargetMarker = None
              ExpectedBuildToken = token true guardJobName 1 }
            :: (corpus
                @ [ { IsGuard = true
                      JobName = guardJobName
                      BuildNumber = 2
                      Definition = probe
                      ExpectedTargetMarker = None
                      ExpectedBuildToken = token true guardJobName 2 } ])

    let internal executeScheduled
        (runOne: string -> int -> JobDefinition -> string option -> string option -> Result<Trace, string>)
        (scheduled: ScheduledBuild list)
        =
        scheduled
        |> List.fold
            (fun (acc, halted) item ->
                match halted with
                | Some why -> ((item.IsGuard, Error $"sequence halted: {why}") :: acc, halted)
                | None ->
                    let result =
                        runOne
                            item.JobName
                            item.BuildNumber
                            item.Definition
                            item.ExpectedTargetMarker
                            item.ExpectedBuildToken

                    let nextHalt =
                        if item.IsGuard then
                            runtimeGuardResultFailure result
                            |> Option.map (fun why -> $"runtime guard failed ({why})")
                        else
                            match result with
                            | Error why -> Some $"a prior build failed to run ({why})"
                            | Ok _ -> None

                    ((item.IsGuard, result) :: acc, nextHalt))
            ([], None)
        |> fun (acc, _) -> List.rev acc

    let internal jobXml (buildPath: string option) (script: string) =
        "<flow-definition plugin=\"workflow-job\"><description/><keepDependencies>false</keepDependencies>"
        + "<properties>"
        // PERFORMANCE_OPTIMIZED deliberately: the differential compares SEMANTICS,
        // and MAX_SURVIVABILITY costs ~6.9 fsyncs per step without changing any
        // observable output. Durability is compared separately, not here.
        + "<org.jenkinsci.plugins.workflow.job.properties.DurabilityHintJobProperty>"
        + "<hint>PERFORMANCE_OPTIMIZED</hint>"
        + "</org.jenkinsci.plugins.workflow.job.properties.DurabilityHintJobProperty>"
        + buildPathProperty buildPath
        + "</properties>"
        + "<definition class=\"org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition\" plugin=\"workflow-cps\">"
        + $"<script>{xmlEscape script}</script><sandbox>true</sandbox></definition>"
        + "<triggers/><disabled>false</disabled></flow-definition>"

    /// CpsScmFlowDefinition: the job POINTS AT the SCM; Jenkins obtains the
    /// Jenkinsfile from it (lightweight) and Declarative auto-checks-out.
    let private scmJobXmlWithBuildPath (buildPath: string option) (attestDefinition: bool) (spec: ScmSpec) =
        let lightweight = if attestDefinition then "false" else "true"
        "<flow-definition plugin=\"workflow-job\"><description/><keepDependencies>false</keepDependencies>"
        + "<properties>"
        + "<org.jenkinsci.plugins.workflow.job.properties.DurabilityHintJobProperty>"
        + "<hint>PERFORMANCE_OPTIMIZED</hint>"
        + "</org.jenkinsci.plugins.workflow.job.properties.DurabilityHintJobProperty>"
        + buildPathProperty buildPath
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

    let scmJobXml (attestDefinition: bool) (spec: ScmSpec) =
        scmJobXmlWithBuildPath None attestDefinition spec

    /// FG-110. Run a SEQUENCE of requested builds on ONE corpus job and return
    /// a trace per build. The corpus job is created once, its definition
    /// UPDATED between builds (a sequence's scripts may differ), and deleted
    /// only at the end, so build history exists and `changed`/`fixed`/
    /// `regression` can select against a real previous result. Optional runtime
    /// guards use one sibling job and never enter that history. Each requested
    /// build is polled BY NUMBER — build k is corpus build #k — and its
    /// workspace is hashed after each build, exactly where it lives.
    let runMany
        (cfg: JenkinsConfig)
        (envReplacements: (string * string) list)
        (jobName: string)
        (builds: JobDefinition list)
        : Result<Trace, string> list =
        let prepared =
            match cfg.RuntimeGuard with
            | None -> Ok(builds, None, None)
            | Some guard ->
                let nonce =
                    Convert.ToHexString(RandomNumberGenerator.GetBytes 16).ToLowerInvariant()
                let marker = targetRuntimeMarker guard.CaseSha nonce

                if cfg.BuildPath <> Some guard.BuildPath then
                    Error "runtime guard and Jenkins job PATH do not name the same exact value"
                else
                    builds
                    |> List.fold
                        (fun state definition ->
                            match state, definition with
                            | Error why, _ -> Error why
                            | Ok _, FromScm _ -> Error "runtime-guarded SCM definitions are not supported"
                            | Ok accumulated, Inline script ->
                                injectTargetRuntimeGuard guard nonce marker script
                                |> Result.map (fun transformed -> Inline transformed :: accumulated))
                        (Ok [])
                    |> Result.map (fun reversed -> List.rev reversed, Some marker, Some nonce)

        match prepared with
        | Error why -> builds |> List.map (fun _ -> Error $"Jenkins runtime guard refused: {why}")
        | Ok(preparedBuilds, targetMarker, buildTokenNonce) ->
            try
            let field, value = crumb cfg
            let guardJobName = runtimeGuardJobName jobName
            let cleanupNames = cleanupJobNames jobName cfg.RuntimeGuard.IsSome

            let postWithLocation (path: string) (content: HttpContent option) =
                use req = new HttpRequestMessage(HttpMethod.Post, $"{cfg.BaseUrl}{path}")
                req.Headers.Add(field, value)
                content |> Option.iter (fun c -> req.Content <- c)
                use r = client.Send req
                let location =
                    if isNull r.Headers.Location then None
                    else Some(r.Headers.Location.ToString())
                int r.StatusCode, location

            let post (path: string) (content: HttpContent option) =
                postWithLocation path content |> fst

            for cleanupName in cleanupNames do
                post $"/job/{cleanupName}/doDelete" None |> ignore

            let runOneInner
                (activeJobName: string)
                (buildNumber: int)
                (definition: JobDefinition)
                (expectedTargetMarker: string option)
                (expectedBuildToken: string option)
                : Result<Trace, string> =
                let xml () =
                    let body =
                        match definition with
                        | Inline script -> jobXml cfg.BuildPath script
                        | FromScm spec ->
                            scmJobXmlWithBuildPath
                                cfg.BuildPath
                                (Environment.GetEnvironmentVariable "FOGELL_SCM_ATTESTATION" = "fg177-probes-v1")
                                spec

                    new StringContent(body, Encoding.UTF8, "application/xml")

                let ready =
                    if buildNumber = 1 then
                        let created = post $"/createItem?name={activeJobName}" (Some(xml ()))

                        if created = 200 || created = 201 then
                            Ok()
                        else
                            Error $"createItem returned HTTP {created}"
                    else
                        // update the definition in place; history survives
                        let updated = post $"/job/{activeJobName}/config.xml" (Some(xml ()))
                        if updated = 200 then Ok() else Error $"config.xml update returned HTTP {updated}"

                match ready with
                | Error e -> Error e
                | Ok() ->

                // FG-103: the trigger's status propagates — a stale crumb or a 409
                // otherwise means five minutes of blind polling for a build that
                // never exists, blamed on "did not reach a terminal state".
                let queueId =
                    let triggerStatus, location =
                        postWithLocation
                            (buildTriggerPath activeJobName cfg.BuildPath expectedBuildToken)
                            None

                    match triggerStatus, location with
                    | (200 | 201), Some value ->
                        match queueItemId value with
                        | Ok id -> id
                        | Error why -> failwith why
                    | (200 | 201), None ->
                        failwith "Jenkins trigger did not return a queue-item Location"
                    | other, _ -> failwith $"build trigger returned HTTP {other}"

                let mutable ownedBuildNumber = None
                let mutable queueAttempts = 0
                let mutable queueFailure = None

                while ownedBuildNumber.IsNone && queueFailure.IsNone && queueAttempts < 600 do
                    Threading.Thread.Sleep 100
                    queueAttempts <- queueAttempts + 1

                    try
                        let queueJson =
                            client.GetStringAsync($"{cfg.BaseUrl}/queue/item/{queueId}/api/json").Result

                        match queueExecutableNumber queueJson with
                        | Ok(Some number) -> ownedBuildNumber <- Some number
                        | Ok None -> ()
                        | Error why -> queueFailure <- Some why
                    with _ -> ()

                match queueFailure, ownedBuildNumber with
                | Some why, _ -> failwith $"owned Jenkins queue item {queueId} was refused ({why})"
                | None, None -> failwith $"owned Jenkins queue item {queueId} did not become executable"
                | None, Some observed when observed <> buildNumber ->
                    failwith
                        $"owned Jenkins queue item {queueId} became build {observed}, expected history-pristine build {buildNumber}"
                | None, Some _ -> ()

                // poll THIS build number to a terminal state
                let mutable result = None
                let mutable attempts = 0

                while result.IsNone && attempts < 600 do
                    Threading.Thread.Sleep 500
                    attempts <- attempts + 1

                    try
                        let body =
                            client.GetStringAsync($"{cfg.BaseUrl}/job/{activeJobName}/{buildNumber}/api/json").Result

                        if Regex.IsMatch(body, "\"building\":false") then
                            let m = Regex.Match(body, "\"result\":\"([A-Z_]+)\"")
                            if m.Success then result <- Some(m.Groups[1].Value.ToLowerInvariant())
                    with _ ->
                        ()

                match result with
                | None -> Error "jenkins build did not reach a terminal state"
                | Some terminal ->
                    cfg.RuntimeGuard
                    |> Option.iter (fun guard ->
                        let parameterTree = Uri.EscapeDataString "queueId,actions[parameters[name,value]]"
                        let parameterJson =
                            client.GetStringAsync(
                                $"{cfg.BaseUrl}/job/{activeJobName}/{buildNumber}/api/json?tree={parameterTree}"
                            ).Result

                        match validateBuildPathParameter guard.BuildPath parameterJson with
                        | Ok() -> ()
                        | Error why -> failwith why

                        match expectedBuildToken with
                        | Some token ->
                            match validateBuildOwnership queueId token parameterJson with
                            | Ok() -> ()
                            | Error why -> failwith why
                        | None -> failwith "runtime-guarded build had no expected ownership token"

                        match definition with
                        | FromScm _ -> failwith "runtime-guarded SCM definitions are not supported"
                        | Inline expectedScript ->
                            let replay =
                                client.GetStringAsync(
                                    $"{cfg.BaseUrl}/job/{activeJobName}/{buildNumber}/replay/"
                                ).Result

                            match replayMainScript replay with
                            | Ok observed when String.Equals(observed, expectedScript, StringComparison.Ordinal) -> ()
                            | Ok _ -> failwith "executed Jenkins definition differed from the scheduled definition"
                            | Error why -> failwith why)

                    let console =
                        client.GetStringAsync($"{cfg.BaseUrl}/job/{activeJobName}/{buildNumber}/consoleText").Result

                    exportRawConsole cfg.RawConsoleExport activeJobName buildNumber console

                    let rawLinesUnattested = console.Replace("\r\n", "\n").Split '\n'

                    let rawLines =
                        match expectedTargetMarker with
                        | None -> rawLinesUnattested
                        | Some marker ->
                            match
                                validateAndRemoveTargetRuntimeMarker
                                    cfg.DeclaresTimestamps
                                    marker
                                    rawLinesUnattested
                            with
                            | Ok lines -> lines
                            | Error why -> failwith why

                    cfg.RuntimeGuard
                    |> Option.iter (fun guard ->
                        match validateRuntimeGuardNode guard.RequiredNode rawLines with
                        | Ok() -> ()
                        | Error why -> failwith why)

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
                                    $"{cfg.BaseUrl}/job/{activeJobName}/{buildNumber}/api/json?tree={tree}"
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
                        | Some root, _ -> Trace.hashWorkspace (IO.Path.Combine(root, activeJobName))
                        | None, Some template -> Trace.collectRemote (template.Replace("{job}", activeJobName))
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

                        let ws = defaultArg fromBanner $"/var/jenkins_home/workspace/{activeJobName}"
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
            let runOne
                (activeJobName: string)
                (buildNumber: int)
                (definition: JobDefinition)
                (expectedTargetMarker: string option)
                (expectedBuildToken: string option)
                : Result<Trace, string> =
                try
                    runOneInner
                        activeJobName
                        buildNumber
                        definition
                        expectedTargetMarker
                        expectedBuildToken
                with ex ->
                    Error ex.Message

            let scheduled =
                match cfg.RuntimeGuard with
                | None -> scheduleBuilds jobName guardJobName None None None preparedBuilds
                | Some guard ->
                    scheduleBuilds
                        jobName
                        guardJobName
                        (Some(Inline(runtimeGuardScript guard)))
                        targetMarker
                        buildTokenNonce
                        preparedBuilds

            let scheduledResults = executeScheduled runOne scheduled

            let guardFailure =
                scheduledResults
                |> List.tryPick (fun (isGuard, result) ->
                    if not isGuard then None
                    else
                        runtimeGuardResultFailure result)

            let results =
                match guardFailure with
                | Some why -> builds |> List.map (fun _ -> Error $"Jenkins runtime guard failed: {why}")
                | None ->
                    scheduledResults
                    |> List.choose (fun (isGuard, result) -> if isGuard then None else Some result)

            // Best-effort cleanup AFTER the evidence is safe: a delete failure
            // must not replace collected traces (the next run of this case
            // deletes the job first anyway).
            for cleanupName in cleanupNames do
                try
                    post $"/job/{cleanupName}/doDelete" None |> ignore
                with _ ->
                    ()

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
