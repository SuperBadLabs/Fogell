namespace Fogell.Controller.Api

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open Microsoft.Win32.SafeHandles
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Fogell.Domain
open Fogell.Store

/// FG-060. The public API.
///
/// Six endpoints, all tenant-scoped in the path. Authorization is checked before
/// anything else on every route — including before the path parameters are parsed
/// — so an unauthenticated caller cannot learn whether an organization or build
/// exists by comparing 404 against 400.
type ApiState =
    { Store: Store
      Auth: Authorization.Config
      /// Placement is controller policy. A bearer is not a placement grant.
      TrustPool: string
      /// Durable controller root. Artifact URLs are derived from build UUIDs,
      /// never from caller-supplied filesystem roots.
      StateRoot: string
      MaxPipelineBytes: int
      MaxLogChunks: int }

module Router =

    type private ArtifactOpen =
        | ArtifactOpened of FileStream
        | InvalidArtifactPath
        | ArtifactSnapshotNotFound
        | ArtifactNotFound
        | ArtifactPlatformUnsupported
        | ArtifactUnavailable

    [<Literal>]
    let private OpenReadOnly = 0

    [<Literal>]
    let private OpenNonBlocking = 0x800

    [<Literal>]
    let private OpenCloseOnExec = 0x80000

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openFile(string path, int flags)

    [<DllImport("libc", EntryPoint = "realpath", SetLastError = true)>]
    extern nativeint private realpath(string path, nativeint resolvedPath)

    [<DllImport("libc", EntryPoint = "free")>]
    extern void private free(nativeint pointer)

    let private physicalPath path =
        let pointer = realpath (path, nativeint 0)

        if pointer = nativeint 0 then
            None
        else
            try
                Marshal.PtrToStringUTF8 pointer |> Option.ofObj
            finally
                free pointer

    let private isWithin (root: string) (path: string) =
        let prefix = root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
        path.StartsWith(prefix, StringComparison.Ordinal)

    let private classifyOpenError () =
        match Marshal.GetLastPInvokeError() with
        | 2
        | 20
        | 40 -> ArtifactNotFound
        | _ -> ArtifactUnavailable

    let private classifySnapshotOpenError () =
        match Marshal.GetLastPInvokeError() with
        | 2 -> ArtifactSnapshotNotFound
        | 20
        | 40 -> ArtifactNotFound
        | _ -> ArtifactUnavailable

    let internal artifactPathSegments (rawPath: string) =
        if String.IsNullOrWhiteSpace rawPath || rawPath.IndexOf '\u0000' >= 0 then
            None
        else
            let values = rawPath.Split('/', StringSplitOptions.None)

            if
                values.Length = 0
                || values
                   |> Array.exists (fun value ->
                       String.IsNullOrEmpty value || value = "." || value = "..")
            then
                None
            else
                Some values

    /// Open through a descriptor before trusting the resolved target. O_NOFOLLOW
    /// rejects a final symlink and /proc/self/fd binds the check to the object
    /// actually opened, so a pathname swap cannot redirect response bytes outside
    /// this attempt's artifact root between validation and streaming.
    let private tryOpenArtifact (stateRoot: string) (organizationId: Guid) (attemptId: Guid) (rawPath: string) =
        // FG-238: O_DIRECTORY/O_NOFOLLOW are per-architecture; an untabulated
        // architecture is a platform refusal, not an open with the wrong bits.
        match artifactPathSegments rawPath, LinuxOpenFlags.current with
        | None, _ -> InvalidArtifactPath
        | Some _, _ when not (OperatingSystem.IsLinux()) -> ArtifactPlatformUnsupported
        | Some _, Error _ -> ArtifactPlatformUnsupported
        | Some values, Ok table ->
            // A missing snapshot parent is a legacy-migration signal only
            // beneath the real organization workspace. Refuse symlinked
            // workspace ancestors before an ENOENT can cause filesystem work.
            let workspaceRoot =
                Path.Combine(stateRoot, "workspaces", organizationId.ToString "N")
            let workspaceDescriptor =
                openFile (
                    workspaceRoot,
                    OpenReadOnly
                    ||| OpenNonBlocking
                    ||| table.Directory
                    ||| table.NoFollow
                    ||| OpenCloseOnExec)
            let workspaceStatus =
                if workspaceDescriptor < 0 then
                    Error(classifyOpenError ())
                else
                    use _workspaceHandle = new SafeFileHandle(nativeint workspaceDescriptor, true)
                    match physicalPath stateRoot, physicalPath $"/proc/self/fd/{workspaceDescriptor}" with
                    | Some physicalStateRoot, Some physicalWorkspace ->
                        let expectedPhysicalWorkspace =
                            Path.Combine(
                                physicalStateRoot,
                                "workspaces",
                                organizationId.ToString "N")
                        if String.Equals(physicalWorkspace, expectedPhysicalWorkspace, StringComparison.Ordinal) then
                            Ok()
                        else
                            Error ArtifactNotFound
                    | _ -> Error ArtifactUnavailable

            let snapshotParent =
                Path.Combine(
                    stateRoot,
                    "workspaces",
                    organizationId.ToString "N",
                    "_artifact-snapshots")

            let parentDescriptor =
                if Result.isOk workspaceStatus then
                    openFile (
                        snapshotParent,
                        OpenReadOnly
                        ||| OpenNonBlocking
                        ||| table.Directory
                        ||| table.NoFollow
                        ||| OpenCloseOnExec)
                else
                    -1

            match workspaceStatus with
            | Error result -> result
            | Ok () when parentDescriptor < 0 -> classifySnapshotOpenError ()
            | Ok () ->
                use _parentHandle = new SafeFileHandle(nativeint parentDescriptor, true)
                let parentDescriptorPath = $"/proc/self/fd/{parentDescriptor}"

                match physicalPath stateRoot, physicalPath parentDescriptorPath with
                | Some physicalStateRoot, Some physicalParent ->
                    let expectedPhysicalParent =
                        Path.Combine(
                            physicalStateRoot,
                            "workspaces",
                            organizationId.ToString "N",
                            "_artifact-snapshots")

                    if not (String.Equals(physicalParent, expectedPhysicalParent, StringComparison.Ordinal)) then
                        ArtifactNotFound
                    else
                        let snapshotRoot = Path.Combine(parentDescriptorPath, attemptId.ToString "N")

                        let rootDescriptor =
                            openFile (
                                snapshotRoot,
                                OpenReadOnly
                                ||| OpenNonBlocking
                                ||| table.Directory
                                ||| table.NoFollow
                                ||| OpenCloseOnExec)

                        if rootDescriptor < 0 then
                            classifySnapshotOpenError ()
                        else
                            use _rootHandle = new SafeFileHandle(nativeint rootDescriptor, true)
                            let rootDescriptorPath = $"/proc/self/fd/{rootDescriptor}"

                            match physicalPath rootDescriptorPath with
                            | None -> ArtifactUnavailable
                            | Some physicalRoot ->
                                let expectedPhysicalRoot =
                                    Path.Combine(physicalParent, attemptId.ToString "N")

                                if not (String.Equals(physicalRoot, expectedPhysicalRoot, StringComparison.Ordinal)) then
                                    ArtifactNotFound
                                else
                                    let candidate =
                                        values
                                        |> Array.fold
                                            (fun current segment -> Path.Combine(current, segment))
                                            rootDescriptorPath

                                    let descriptor =
                                        openFile (
                                            candidate,
                                            OpenReadOnly ||| OpenNonBlocking ||| table.NoFollow ||| OpenCloseOnExec)

                                    if descriptor < 0 then
                                        classifyOpenError ()
                                    else
                                        let handle = new SafeFileHandle(nativeint descriptor, true)

                                        try
                                            use stream = new FileStream(handle, FileAccess.Read, 65536, false)
                                            let descriptorPath = $"/proc/self/fd/{descriptor}"

                                            match physicalPath descriptorPath with
                                            | Some openedPath
                                                when isWithin physicalRoot openedPath
                                                     && not (Directory.Exists descriptorPath)
                                                     && stream.CanSeek ->
                                                // Force metadata acquisition before returning a
                                                // 200 response. A non-regular or unreadable object
                                                // must fail before headers claim a byte length.
                                                stream.Length |> ignore
                                                // A raw SafeFileHandle cannot be marked async in
                                                // .NET. Reopen the still-live descriptor through
                                                // procfs, never the caller-controlled pathname, so
                                                // response reads are async while the inode remains
                                                // the one whose physical containment was verified.
                                                let responseStream =
                                                    new FileStream(
                                                        descriptorPath,
                                                        FileMode.Open,
                                                        FileAccess.Read,
                                                        FileShare.ReadWrite ||| FileShare.Delete,
                                                        65536,
                                                        FileOptions.Asynchronous ||| FileOptions.SequentialScan)
                                                try
                                                    responseStream.Length |> ignore
                                                    ArtifactOpened responseStream
                                                with _ ->
                                                    responseStream.Dispose()
                                                    reraise()
                                            | _ ->
                                                ArtifactNotFound
                                        with _ ->
                                            handle.Dispose()
                                            ArtifactUnavailable
                | _ -> ArtifactUnavailable

    let private json (ctx: HttpContext) (status: int) (payload: 'a) =
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.WriteAsJsonAsync payload

    let private fail (ctx: HttpContext) status code message position =
        let payload: ErrorResponse =
            { Code = code; Message = message; Position = position }

        json ctx status payload

    /// Deny by default. Returns Some when the caller may proceed.
    let private authorized (state: ApiState) (ctx: HttpContext) =
        let header =
            match ctx.Request.Headers.TryGetValue "authorization" with
            | true, v when v.Count > 0 -> Some(v.[0])
            | _ -> None

        Authorization.authorize state.Auth header

    let private header (ctx: HttpContext) name =
        match ctx.Request.Headers.TryGetValue name with
        | true, v when v.Count > 0 && not (String.IsNullOrWhiteSpace v.[0]) -> Some(v.[0])
        | _ -> None

    let private guid (raw: string) =
        match Guid.TryParse raw with
        | true, g -> Some g
        | _ -> None

    let private readBoundedBody (ctx: HttpContext) maxBytes =
        task {
            if ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value > int64 maxBytes then
                return Error "pipeline_too_large"
            else
                use bytes = new MemoryStream(min maxBytes 65536)
                let buffer = Array.zeroCreate<byte> 16384
                let mutable finished = false
                let mutable tooLarge = false

                while not finished && not tooLarge do
                    let remaining = maxBytes + 1 - int bytes.Length
                    let! count =
                        ctx.Request.Body.ReadAsync(
                            buffer.AsMemory(0, min buffer.Length remaining),
                            ctx.RequestAborted)

                    if count = 0 then
                        finished <- true
                    else
                        bytes.Write(buffer, 0, count)
                        tooLarge <- bytes.Length > int64 maxBytes

                if tooLarge then
                    return Error "pipeline_too_large"
                else
                    return Ok(bytes.ToArray())
        }

    /// POST …/builds — submit a Jenkinsfile.
    ///
    /// The body is the pipeline source. It is PARSED before admission, so a
    /// malformed pipeline is rejected with its error code and source position
    /// rather than becoming a queued build that fails later for reasons the
    /// submitter cannot see.
    let private submit (state: ApiState) (ctx: HttpContext) =
        task {
            if not (authorized state ctx) then
                return! fail ctx 401 "unauthorized" "a valid bearer token is required" None
            else
                match guid (string ctx.Request.RouteValues["organizationId"]),
                      guid (string ctx.Request.RouteValues["projectId"]) with
                | None, _
                | _, None -> return! fail ctx 400 "malformed_identifier" "organization and project must be UUIDs" None
                | Some org, Some project ->
                    if ctx.Request.Headers.ContainsKey "fogell-trust-pool" then
                        return!
                            fail ctx 400 "placement_override_forbidden"
                                "execution placement is controller policy and cannot be selected by a request" None
                    else
                        match header ctx "idempotency-key" with
                        | None ->
                            // Required, not optional: without it a retried submission
                            // silently creates a second build.
                            return!
                                fail ctx 400 "idempotency_key_required"
                                    "an Idempotency-Key header is required so a retry cannot create a second build" None
                        | Some key when Encoding.UTF8.GetByteCount key > 256 ->
                            return! fail ctx 400 "idempotency_key_too_long" "Idempotency-Key must be at most 256 bytes" None
                        | Some key ->
                            let! body = readBoundedBody ctx state.MaxPipelineBytes

                            match body with
                            | Error _ ->
                                return!
                                    fail ctx 413 "pipeline_too_large"
                                        $"pipeline source must be at most {state.MaxPipelineBytes} bytes" None
                            | Ok sourceBytes ->
                                let probe: AdmissionProbe =
                                    { OrganizationId = OrganizationId org
                                      ProjectId = ProjectId project
                                      IdempotencyKey = key
                                      PipelineSource = sourceBytes
                                      RequiredTrustPool = state.TrustPool
                                      RequiredCapabilities = [ "linux" ] }

                                let respond (admission: Fogell.Store.Admission) =
                                    let payload: AdmissionResponse =
                                        { BuildId = string admission.BuildId.Value
                                          NodeId = string admission.NodeId.Value
                                          AttemptId = string admission.AttemptId.Value
                                          Number = admission.Number
                                          WasExisting = admission.WasExisting }

                                    json ctx (if admission.WasExisting then 200 else 201) payload

                                // Resolve an already-bound raw request before decoding, parsing, or
                                // applying today's execution rules. Once a key is durable, every
                                // different byte sequence or placement fingerprint is the same 409
                                // conflict regardless of whether the replacement happens to parse.
                                // A miss creates nothing and takes no authority: AdmitBuild remains
                                // the race arbiter after fresh-source preflight.
                                match state.Store.TryReplayAdmission probe with
                                | Result.Error e when
                                    e.StartsWith("idempotency key is already bound", StringComparison.Ordinal)
                                    ->
                                    return! fail ctx 409 "idempotency_conflict" e None
                                | Result.Error _ ->
                                    return!
                                        fail ctx 503 "admission_unavailable"
                                            "build admission is temporarily unavailable" None
                                | Result.Ok(Some admission) -> return! respond admission
                                | Result.Ok None ->
                                    let sourceResult =
                                        try
                                            Ok(UTF8Encoding(false, true).GetString sourceBytes)
                                        with :? DecoderFallbackException ->
                                            Error "pipeline source must be valid UTF-8"

                                    match sourceResult with
                                    | Error message -> return! fail ctx 400 "invalid_utf8" message None
                                    | Ok source ->
                                        match Fogell.Pipeline.Parser.Parser.parse source with
                                        | Result.Error e ->
                                            return!
                                                fail ctx 422
                                                    (Fogell.Admission.ErrorCode.toWireString e.Code)
                                                    (Fogell.Admission.AdmissionError.render source e)
                                                    (Some(string e.Position))
                                        | Result.Ok pipeline ->
                                            let stages =
                                                Fogell.Ir.Pipeline.flattenStages pipeline.Stages
                                                |> List.map (fun stage -> stage.Name)

                                            let input: NewBuild =
                                                { OrganizationId = probe.OrganizationId
                                                  ProjectId = probe.ProjectId
                                                  IdempotencyKey = probe.IdempotencyKey
                                                  PipelineSource = probe.PipelineSource
                                                  StageNames = stages
                                                  RequiredTrustPool = probe.RequiredTrustPool
                                                  RequiredCapabilities = probe.RequiredCapabilities }

                                            // Parse success is deliberately broader than execution
                                            // capability. Only a fresh key must satisfy the same
                                            // fail-closed persisted preflight as Run.Host before
                                            // binding a build number or idempotency key.
                                            match Fogell.Differential.FogellSide.preflightControllerExecution source with
                                            | Result.Error why ->
                                                return!
                                                    fail ctx 422 "execution_unsupported" why None
                                            | Result.Ok _ ->
                                                match state.Store.AdmitBuild input with
                                                | Result.Error e when
                                                    e.StartsWith("idempotency key is already bound", StringComparison.Ordinal)
                                                    ->
                                                    return! fail ctx 409 "idempotency_conflict" e None
                                                | Result.Error _ ->
                                                    return!
                                                        fail ctx 503 "admission_unavailable"
                                                            "build admission is temporarily unavailable" None
                                                | Result.Ok admission -> return! respond admission
        }
        :> Threading.Tasks.Task

    let private status (state: ApiState) (ctx: HttpContext) =
        task {
            if not (authorized state ctx) then
                return! fail ctx 401 "unauthorized" "a valid bearer token is required" None
            else
                match guid (string ctx.Request.RouteValues["organizationId"]),
                      guid (string ctx.Request.RouteValues["projectId"]),
                      guid (string ctx.Request.RouteValues["buildId"]) with
                | None, _, _
                | _, None, _
                | _, _, None -> return! fail ctx 400 "malformed_identifier" "identifiers must be UUIDs" None
                | Some org, Some project, Some build ->
                    match state.Store.BuildSnapshot(OrganizationId org, ProjectId project, BuildId build) with
                    | None -> return! fail ctx 404 "not_found" "no such build" None
                    | Some(s, cancelled) ->
                        let payload: StatusResponse =
                            { BuildId = string build
                              Status = s
                              CancellationRequested = cancelled }

                        return! json ctx 200 payload
        }
        :> Threading.Tasks.Task

    /// GET …/logs?from=N — progressive read (FG-064).
    let private logs (state: ApiState) (ctx: HttpContext) =
        task {
            if not (authorized state ctx) then
                return! fail ctx 401 "unauthorized" "a valid bearer token is required" None
            else
                match guid (string ctx.Request.RouteValues["organizationId"]),
                      guid (string ctx.Request.RouteValues["projectId"]),
                      guid (string ctx.Request.RouteValues["buildId"]) with
                | None, _, _
                | _, None, _
                | _, _, None -> return! fail ctx 400 "malformed_identifier" "identifiers must be UUIDs" None
                | Some org, Some project, Some build ->
                    let from =
                        match ctx.Request.Query.TryGetValue "from" with
                        | true, v when v.Count > 0 ->
                            match Int32.TryParse v.[0] with
                            | true, n when n >= 0 -> Ok n
                            | _ -> Error()
                        | _ -> Ok 0

                    match from with
                    | Error _ -> return! fail ctx 400 "invalid_log_cursor" "from must be a non-negative integer" None
                    | Ok from ->
                        match
                            state.Store.ReadLogPage(
                                OrganizationId org,
                                ProjectId project,
                                BuildId build,
                                from,
                                state.MaxLogChunks)
                        with
                        | None -> return! fail ctx 404 "not_found" "no such build" None
                        | Some chunks ->
                            let next =
                                match chunks with
                                | [] -> from
                                | _ -> (chunks |> List.map fst |> List.max) + 1

                            let payload: LogResponse =
                                { BuildId = string build
                                  FromSequence = from
                                  NextSequence = next
                                  Chunks = chunks |> List.map (fun (s, b) -> { Sequence = s; Body = b }) }

                            return! json ctx 200 payload
        }
        :> Threading.Tasks.Task

    /// GET …/attempts/{attemptId}/artifacts/{path} — authenticated byte-exact
    /// output from one immutable execution attempt (FG-042b).
    let private artifact (state: ApiState) (ctx: HttpContext) =
        task {
            if not (authorized state ctx) then
                return! fail ctx 401 "unauthorized" "a valid bearer token is required" None
            else
                match guid (string ctx.Request.RouteValues["organizationId"]),
                      guid (string ctx.Request.RouteValues["projectId"]),
                      guid (string ctx.Request.RouteValues["buildId"]),
                      guid (string ctx.Request.RouteValues["attemptId"]) with
                | None, _, _, _
                | _, None, _, _
                | _, _, None, _
                | _, _, _, None -> return! fail ctx 400 "malformed_identifier" "identifiers must be UUIDs" None
                | Some org, Some project, Some build, Some attempt ->
                    // Database ownership is authoritative. Never let a guessed
                    // physical path bypass the organization/project boundary.
                    match
                        state.Store.ArtifactAttemptState(
                            OrganizationId org,
                            ProjectId project,
                            BuildId build,
                            AttemptId attempt)
                    with
                    | None -> return! fail ctx 404 "not_found" "no such build or attempt" None
                    | Some attemptState when attemptState <> "terminal" ->
                        return!
                            fail ctx 409 "artifact_not_ready"
                                "artifacts are available only after attempt completion" None
                    | Some _ ->
                        let artifactPath = string ctx.Request.RouteValues["artifactPath"]
                        let opened = tryOpenArtifact state.StateRoot org attempt artifactPath
                        let opened =
                            match opened with
                            | ArtifactSnapshotNotFound ->
                                let migrate () =
                                    ArtifactSnapshots.finalize state.StateRoot org build attempt
                                    |> Result.map ignore
                                match
                                    state.Store.MigrateLegacyArtifactSnapshot(
                                        OrganizationId org,
                                        ProjectId project,
                                        BuildId build,
                                        AttemptId attempt,
                                        migrate)
                                with
                                | Ok true -> tryOpenArtifact state.StateRoot org attempt artifactPath
                                | Ok false -> ArtifactNotFound
                                | Error _ -> ArtifactUnavailable
                            | value -> value

                        match opened with
                        | InvalidArtifactPath ->
                            return!
                                fail ctx 400 "invalid_artifact_path"
                                    "artifact path must contain only nonempty relative segments" None
                        | ArtifactNotFound ->
                            return! fail ctx 404 "artifact_not_found" "no such artifact" None
                        | ArtifactSnapshotNotFound ->
                            return! fail ctx 404 "artifact_not_found" "no such artifact" None
                        | ArtifactPlatformUnsupported ->
                            return!
                                fail ctx 501 "artifact_platform_unsupported"
                                    "artifact retrieval requires Linux descriptor semantics" None
                        | ArtifactUnavailable ->
                            return!
                                fail ctx 503 "artifact_unavailable"
                                    "artifact storage is temporarily unavailable" None
                        | ArtifactOpened stream ->
                            use stream = stream
                            ctx.Response.StatusCode <- 200
                            ctx.Response.ContentType <- "application/octet-stream"
                            ctx.Response.ContentLength <- Nullable stream.Length
                            ctx.Response.Headers["X-Content-Type-Options"] <- "nosniff"
                            let fileName = Uri.EscapeDataString(Path.GetFileName artifactPath)
                            ctx.Response.Headers["Content-Disposition"] <-
                                $"attachment; filename*=UTF-8''{fileName}"
                            do! stream.CopyToAsync(ctx.Response.Body, 65536, ctx.RequestAborted)
        }
        :> Threading.Tasks.Task

    let private cancel (state: ApiState) (ctx: HttpContext) =
        task {
            if not (authorized state ctx) then
                return! fail ctx 401 "unauthorized" "a valid bearer token is required" None
            else
                match guid (string ctx.Request.RouteValues["organizationId"]),
                      guid (string ctx.Request.RouteValues["projectId"]),
                      guid (string ctx.Request.RouteValues["buildId"]) with
                | None, _, _
                | _, None, _
                | _, _, None -> return! fail ctx 400 "malformed_identifier" "identifiers must be UUIDs" None
                | Some org, Some project, Some build ->
                    match state.Store.RequestCancellation(OrganizationId org, ProjectId project, BuildId build) with
                    | CancellationAccepted -> return! json ctx 202 {| accepted = true; already_requested = false |}
                    // Idempotent: a retry after a client timeout must not look
                    // like a failure, because the caller's intent is satisfied.
                    | AlreadyRequested -> return! json ctx 202 {| accepted = true; already_requested = true |}
                    // These ARE conflicts: the caller must not believe it
                    // cancelled something it did not.
                    | AlreadyTerminal s ->
                        return! fail ctx 409 "already_terminal" $"build already finished with status '{s}'" None
                    | NoSuchBuild -> return! fail ctx 404 "not_found" "no such build" None
        }
        :> Threading.Tasks.Task

    /// GET …/scheduler/explain?capability=a&capability=b — why is work waiting?
    let private explain (state: ApiState) (ctx: HttpContext) =
        task {
            if not (authorized state ctx) then
                return! fail ctx 401 "unauthorized" "a valid bearer token is required" None
            else
                match guid (string ctx.Request.RouteValues["organizationId"]) with
                | None -> return! fail ctx 400 "malformed_identifier" "organization must be a UUID" None
                | Some org ->
                    let caps =
                        match ctx.Request.Query.TryGetValue "capability" with
                        | true, v -> v |> Seq.filter (fun s -> not (String.IsNullOrWhiteSpace s)) |> List.ofSeq
                        | _ -> []

                    let pool =
                        match ctx.Request.Query.TryGetValue "trustPool" with
                        | true, v when v.Count > 0 && not (String.IsNullOrWhiteSpace v.[0]) -> v.[0]
                        | _ -> state.TrustPool

                    let payload: ExplainResponse =
                        { TrustPool = pool
                          Capabilities = caps
                          Explanation = state.Store.ExplainWait(OrganizationId org, pool, caps) }

                    return! json ctx 200 payload
        }
        :> Threading.Tasks.Task

    let private orgPath = "/api/v1/organizations/{organizationId}"

    let map (state: ApiState) (endpoints: IEndpointRouteBuilder) =
        endpoints.MapPost($"{orgPath}/projects/{{projectId}}/builds", RequestDelegate(submit state)) |> ignore
        endpoints.MapGet($"{orgPath}/projects/{{projectId}}/builds/{{buildId}}", RequestDelegate(status state)) |> ignore
        endpoints.MapGet($"{orgPath}/projects/{{projectId}}/builds/{{buildId}}/logs", RequestDelegate(logs state)) |> ignore
        endpoints.MapGet(
            $"{orgPath}/projects/{{projectId}}/builds/{{buildId}}/attempts/{{attemptId}}/artifacts/{{**artifactPath}}",
            RequestDelegate(artifact state))
        |> ignore
        endpoints.MapPost($"{orgPath}/projects/{{projectId}}/builds/{{buildId}}/cancel", RequestDelegate(cancel state)) |> ignore
        endpoints.MapGet($"{orgPath}/scheduler/explain", RequestDelegate(explain state)) |> ignore
        endpoints
