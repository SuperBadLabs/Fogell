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

            match migrator.Migrate() with
            | Error _ ->
                eprintfn "FG-224 startup refused: database migration failed"
                3
            | Ok _ ->
                let runtimeStore = Store(config.RuntimeDatabaseUrl)
                let maintenanceIdentity = Store(config.MaintenanceDatabaseUrl).RuntimeDatabaseIdentity()

                match runtimeStore.RuntimeDatabaseIdentity(), maintenanceIdentity with
                | Error _, _
                | _, Error _ ->
                    eprintfn "FG-224 startup refused: database identity check failed"
                    3
                | Ok runtime, Ok maintenance when runtime.User = maintenance.User ->
                    eprintfn "FG-224 startup refused: runtime and maintenance database identities must differ"
                    3
                | Ok runtime, _ when runtime.IsSuperuser || runtime.BypassesRls ->
                    eprintfn "FG-224 startup refused: runtime database identity may bypass tenant isolation"
                    3
                | Ok _, Ok _ when not (runtimeStore.RuntimeCapabilities()) ->
                    eprintfn "FG-224 startup refused: runtime database capability is incomplete"
                    3
                | Ok _, Ok _ ->
                    let auth =
                        match Authorization.configure config.ApiToken with
                        | Ok value -> value
                        | Error error -> invalidOp error

                    let builder = WebApplication.CreateBuilder()
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
                                (if
                                     runtimeStore.Ping()
                                     && runtimeStore.RuntimeCapabilities()
                                     && ControllerConfig.executionLaunchersReady config
                                 then 200
                                 else 503)
                                ctx))
                    |> ignore

                    Router.map
                        { Store = runtimeStore
                          Auth = auth
                          TrustPool = config.TrustPool
                          MaxPipelineBytes = config.MaxPipelineBytes
                          MaxLogChunks = config.MaxLogChunks }
                        app
                    |> ignore

                    app.Run()
                    0
        with _ ->
            eprintfn "FG-224 startup refused: controller initialization failed"
            4
