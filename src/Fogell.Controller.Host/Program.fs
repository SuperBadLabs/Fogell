module Fogell.Controller.Host.Program

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Fogell.Controller.Api
open Fogell.Store

let private health status (ctx: HttpContext) =
    ctx.Response.StatusCode <- status
    ctx.Response.ContentType <- "application/json; charset=utf-8"
    ctx.Response.WriteAsync(if status = 200 then "{\"status\":\"ready\"}" else "{\"status\":\"unavailable\"}")

let internal readinessStatus databaseReady capabilitiesReady launchersReady stateRootReady =
    if databaseReady () && capabilitiesReady () && launchersReady () && stateRootReady () then 200 else 503

[<RequireQualifiedAccess>]
type internal DatabaseStartupError =
    | PairMismatch
    | MigrationFailed
    | IdentityUnavailable
    | SameIdentity
    | RuntimeMayBypassRls
    | RuntimeCapabilitiesIncomplete

let internal databaseStartupError pairMatches migrate runtime maintenance capabilitiesReady =
    if not (pairMatches ()) then
        Some DatabaseStartupError.PairMismatch
    else
        match migrate () with
        | Error _ -> Some DatabaseStartupError.MigrationFailed
        | Ok _ ->
            match runtime (), maintenance () with
            | Error _, _
            | _, Error _ -> Some DatabaseStartupError.IdentityUnavailable
            | Ok runtime, Ok maintenance when runtime.User = maintenance.User ->
                Some DatabaseStartupError.SameIdentity
            | Ok runtime, _ when runtime.IsSuperuser || runtime.BypassesRls ->
                Some DatabaseStartupError.RuntimeMayBypassRls
            | Ok _, Ok _ when not (capabilitiesReady ()) ->
                Some DatabaseStartupError.RuntimeCapabilitiesIncomplete
            | Ok _, Ok _ -> None

let internal databaseStartupErrorForStores
    (migrator: Store)
    (runtimeStore: Store)
    (maintenanceStore: Store)
    =
    databaseStartupError
        migrator.DatabasePairMatches
        migrator.Migrate
        runtimeStore.RuntimeDatabaseIdentity
        maintenanceStore.RuntimeDatabaseIdentity
        runtimeStore.RuntimeCapabilities

/// FG-232. The content root is the apphost's own directory, never the current
/// working directory, and configuration files are read once rather than
/// watched. ASP.NET Core's default roots its file provider at the cwd and, to
/// reload appsettings.json on change, watches that whole tree with one inotify
/// watch per directory: launched from a home directory of ~268k directories,
/// the controller held 65,361 of the user's 65,536 inotify watches and every other
/// FileSystemWatcher for the user then failed. Every setting the controller acts
/// on is read from its environment by ControllerConfig at startup, so nothing
/// consumes a reload; with the watch disabled the host holds no inotify instance
/// at all, whatever directory it is started from. The reload switch is the
/// host's own `hostBuilder:reloadConfigOnChange` key, supplied as a host
/// argument so that no environment variable is needed; the process's real argv
/// is not host input and stays ignored.
let internal contentRootPath = AppContext.BaseDirectory

[<Literal>]
let internal reloadConfigOnChangeSwitch = "--hostBuilder:reloadConfigOnChange=false"

let internal hostOptions () =
    WebApplicationOptions(ContentRootPath = contentRootPath, Args = [| reloadConfigOnChangeSwitch |])

/// FG-026b. The startup trigger: one bounded classification pass per
/// organization before the worker claims anything. A failure is reported and
/// does not refuse startup, because the periodic lease-expiry pass repeats the
/// same classification on every scan; rows still under an unexpired lease of a
/// dead controller are classified once that lease expires, bounded by
/// FOGELL_WORKER_LEASE_SECONDS.
let internal reconcileEffectsAtStartup
    (organizations: unit -> Fogell.Domain.OrganizationId list)
    (reconcile: Fogell.Domain.OrganizationId -> Result<EffectCheckpoint list, string>)
    (report: Fogell.Domain.OrganizationId -> Result<EffectCheckpoint list, string> -> unit)
    =
    let listed =
        try
            Ok(organizations ())
        with ex ->
            Error ex.Message

    match listed with
    | Error error -> report (Fogell.Domain.OrganizationId System.Guid.Empty) (Error error)
    | Ok organizations ->
        for org in organizations do
            let outcome =
                try
                    reconcile org
                with ex ->
                    Error ex.Message

            report org outcome

[<EntryPoint>]
let main _ =
    match ControllerConfig.load () with
    | Error error ->
        eprintfn "FG-224 startup refused: %s" error
        2
    | Ok config ->
        try
            // The migration capability is constructed for startup only and is
            // not retained in request state or injected into the worker.
            let migrator = Store(config.RuntimeDatabaseUrl, config.MaintenanceDatabaseUrl)
            let runtimeStore = Store(config.RuntimeDatabaseUrl)
            let maintenanceStore = Store(config.MaintenanceDatabaseUrl)

            match
                databaseStartupErrorForStores migrator runtimeStore maintenanceStore
            with
            | Some DatabaseStartupError.PairMismatch ->
                    eprintfn "FG-224 startup refused: runtime and maintenance capabilities target different databases"
                    3
            | Some DatabaseStartupError.MigrationFailed ->
                    eprintfn "FG-224 startup refused: database migration failed"
                    3
            | Some DatabaseStartupError.IdentityUnavailable ->
                    eprintfn "FG-224 startup refused: database identity check failed"
                    3
            | Some DatabaseStartupError.SameIdentity ->
                    eprintfn "FG-224 startup refused: runtime and maintenance database identities must differ"
                    3
            | Some DatabaseStartupError.RuntimeMayBypassRls ->
                    eprintfn "FG-224 startup refused: runtime database identity may bypass tenant isolation"
                    3
            | Some DatabaseStartupError.RuntimeCapabilitiesIncomplete ->
                    eprintfn "FG-224 startup refused: runtime database capability is incomplete"
                    3
            | None ->
                    let stateRootReadiness =
                        ControllerConfig.createStateRootReadinessCache config

                    let auth =
                        match Authorization.configure config.ApiToken with
                        | Ok value -> value
                        | Error error -> invalidOp error

                    let builder = WebApplication.CreateBuilder(hostOptions ())
                    builder.WebHost.UseUrls config.ListenUrl |> ignore
                    builder.WebHost.ConfigureKestrel(fun options ->
                        // The public limit is on DECODED pipeline bytes. Kestrel's
                        // chunked-body accounting includes transport framing and
                        // varies with segmentation, so no fixed transport margin
                        // preserves that contract. Router.readBoundedBody is the
                        // sole size authority and retains at most max + 1 bytes.
                        options.Limits.MaxRequestBodySize <- Nullable())
                    |> ignore
                    builder.Services.AddSingleton<ControllerConfig>(config) |> ignore
                    builder.Services.AddSingleton<Store>(runtimeStore) |> ignore
                    builder.Services.AddHostedService<LocalWorker>() |> ignore
                    let app = builder.Build()

                    app.UseExceptionHandler(fun errorApp ->
                        errorApp.Run(fun ctx ->
                            ctx.Response.StatusCode <- 500
                            ctx.Response.ContentType <- "application/json; charset=utf-8"
                            ctx.Response.WriteAsync "{\"code\":\"internal_error\",\"message\":\"request failed\",\"position\":null}"))
                    |> ignore

                    app.MapGet("/health/live", RequestDelegate(fun ctx -> health 200 ctx)) |> ignore
                    app.MapGet(
                        "/health/ready",
                        RequestDelegate(fun ctx ->
                            health
                                (readinessStatus
                                    runtimeStore.Ping
                                    runtimeStore.RuntimeCapabilities
                                    (fun () -> ControllerConfig.executionLaunchersReady config)
                                    (fun () -> stateRootReadiness.Cached()))
                                ctx))
                    |> ignore

                    Router.map
                        { Store = runtimeStore
                          Auth = auth
                          TrustPool = config.TrustPool
                          StateRoot = config.StateRoot
                          MaxPipelineBytes = config.MaxPipelineBytes
                          MaxLogChunks = config.MaxLogChunks }
                        app
                    |> ignore

                    reconcileEffectsAtStartup
                        runtimeStore.OrganizationIds
                        (fun org -> runtimeStore.ReconcileStaleEffects(org, "controller_startup"))
                        (fun org outcome ->
                            match outcome with
                            | Ok [] -> ()
                            | Ok classified ->
                                for checkpoint in classified do
                                    eprintfn
                                        "FG-026b startup reconciliation: effect %s for attempt %O in organization %O is uncertain; operator reconciliation is required"
                                        checkpoint.EffectKey
                                        checkpoint.AttemptId.Value
                                        org.Value
                            | Error error ->
                                eprintfn "FG-026b startup reconciliation failed for organization %O: %s" org.Value error)

                    app.Run()
                    0
        with _ ->
            eprintfn "FG-224 startup refused: controller initialization failed"
            4
