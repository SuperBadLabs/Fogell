namespace Fogell.Controller.Api

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Fogell.Domain
open Fogell.Store

/// FG-060. The public API.
///
/// Five endpoints, all tenant-scoped in the path. Authorization is checked before
/// anything else on every route — including before the path parameters are parsed
/// — so an unauthenticated caller cannot learn whether an organization or build
/// exists by comparing 404 against 400.
type ApiState =
    { Store: Store
      Auth: Authorization.Config
      /// Trust pool assigned to a submission that does not name one.
      DefaultTrustPool: string }

module Router =

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
                    match header ctx "idempotency-key" with
                    | None ->
                        // Required, not optional: without it a retried submission
                        // silently creates a second build.
                        return!
                            fail ctx 400 "idempotency_key_required"
                                "an Idempotency-Key header is required so a retry cannot create a second build" None
                    | Some key when key.Length > 256 ->
                        return! fail ctx 400 "idempotency_key_too_long" "Idempotency-Key must be at most 256 bytes" None
                    | Some key ->
                        use reader = new IO.StreamReader(ctx.Request.Body)
                        let! source = reader.ReadToEndAsync()

                        match Fogell.Pipeline.Parser.Parser.parse source with
                        | Result.Error e ->
                            return!
                                fail ctx 422
                                    (Fogell.Admission.ErrorCode.toWireString e.Code)
                                    e.Message
                                    (Some(string e.Position))
                        | Result.Ok pipeline ->
                            let stages =
                                Fogell.Ir.Pipeline.flattenStages pipeline.Stages
                                |> List.map (fun s -> s.Name)

                            let trustPool =
                                header ctx "fogell-trust-pool" |> Option.defaultValue state.DefaultTrustPool

                            let input =
                                { OrganizationId = OrganizationId org
                                  ProjectId = ProjectId project
                                  IdempotencyKey = key
                                  StageNames = stages
                                  RequiredTrustPool = trustPool
                                  RequiredCapabilities = [ "linux" ] }

                            match state.Store.AdmitBuild input with
                            | Result.Error e -> return! fail ctx 409 "admission_refused" e None
                            | Result.Ok a ->
                                let payload: AdmissionResponse =
                                    { BuildId = string a.BuildId.Value
                                      NodeId = string a.NodeId.Value
                                      AttemptId = string a.AttemptId.Value
                                      Number = a.Number
                                      WasExisting = a.WasExisting }

                                // 200 for a replay, 201 for a fresh admission —
                                // the status code carries the same fact as the body.
                                return! json ctx (if a.WasExisting then 200 else 201) payload
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
                            | true, n when n >= 0 -> n
                            | _ -> 0
                        | _ -> 0

                    match state.Store.ReadLog(OrganizationId org, ProjectId project, BuildId build, from) with
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
                        | _ -> state.DefaultTrustPool

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
        endpoints.MapPost($"{orgPath}/projects/{{projectId}}/builds/{{buildId}}/cancel", RequestDelegate(cancel state)) |> ignore
        endpoints.MapGet($"{orgPath}/scheduler/explain", RequestDelegate(explain state)) |> ignore
        endpoints
