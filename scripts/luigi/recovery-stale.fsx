// FG-270 proof helper. Source seed runs only while the owned Luigi service is
// quiesced; check runs against its explicitly named disposable restore database.
#r "/app/tools/Fogell.Recovery/bin/Release/net10.0/Npgsql.dll"
#r "/app/tools/Fogell.Recovery/bin/Release/net10.0/Microsoft.Extensions.Logging.Abstractions.dll"
#r "/app/tools/Fogell.Recovery/bin/Release/net10.0/Fogell.Domain.dll"
#r "/app/tools/Fogell.Recovery/bin/Release/net10.0/Fogell.Store.dll"

open System
open System.IO
open System.Text.Json
open Fogell.Domain
open Fogell.Store

let connection = Environment.GetEnvironmentVariable "FOGELL_MAINTENANCE_DATABASE_URL"
let builder = Npgsql.NpgsqlConnectionStringBuilder connection
if builder.Database <> "fogell" && not (System.Text.RegularExpressions.Regex.IsMatch(builder.Database, "^fogell_luigi_restore_[a-z0-9_]+$")) then
    failwith "stale authority probe requires the owned Luigi source or restore database"
let store = Store(connection, connection)
let owner = "fg270-luigi-pre-restore-authority"
let args = fsi.CommandLineArgs |> Array.skip 1
match args with
| [| "seed"; receiptPath |] ->
    let org, project = OrganizationId(Guid.NewGuid()), ProjectId(Guid.NewGuid())
    store.CreateProject(org, "recovery-" + org.Value.ToString("N"), project, "stale-authority")
    let admission =
        { OrganizationId = org; ProjectId = project; IdempotencyKey = Guid.NewGuid().ToString()
          PipelineSource = Text.Encoding.UTF8.GetBytes "pipeline { agent any stages { stage('probe') { steps { echo 'probe' } } } }"
          StageNames = ["probe"]; RequiredTrustPool = "trusted-linux"; RequiredCapabilities = ["linux"] }
        |> store.AdmitBuild
        |> function Ok value -> value | Error e -> failwith e
    let fence =
        store.OfferAttempt(org, admission.AttemptId, owner, 3600)
        |> function Ok value -> value | Error e -> failwith e
    if not (store.AcceptAttempt(org, admission.AttemptId, fence, owner)) then failwith "accept failed"
    if not (store.AppendLogFenced(org, admission.BuildId, admission.AttemptId, fence, owner, 0, "pre-backup evidence")) then
        failwith "live authority control failed"
    let receipt = {| organization = org.Value; project = project.Value; build = admission.BuildId.Value;
                    attempt = admission.AttemptId.Value; fence = fence.Value; epoch = store.CurrentRestoreEpoch().Value |}
    File.WriteAllText(receiptPath, JsonSerializer.Serialize receipt)
    printfn "seeded pre-restore authority"
| [| "check"; receiptPath |] ->
    use document = JsonDocument.Parse(File.ReadAllText receiptPath)
    let root = document.RootElement
    let org = OrganizationId(root.GetProperty("organization").GetGuid())
    let build = BuildId(root.GetProperty("build").GetGuid())
    let attempt = AttemptId(root.GetProperty("attempt").GetGuid())
    let fence = Fence(root.GetProperty("fence").GetInt64())
    if store.CurrentRestoreEpoch().Value <= root.GetProperty("epoch").GetInt64() then failwith "epoch did not advance"
    if store.AppendLogFenced(org, build, attempt, fence, owner, 1, "STALE MUST NOT PUBLISH") then failwith "stale log published"
    match store.PublishTerminal(org, attempt, fence, owner, Success) with
    | Ok() -> failwith "stale terminal published"
    | Error _ -> ()
    match store.AttemptState(org, attempt) with
    | Some("reconciliation_required", _, _) -> ()
    | _ -> failwith "stale attempt lost reconciliation evidence"
    printfn "{\"passed\":true,\"stale_log_rejected\":true,\"stale_terminal_rejected\":true}"
| [| "cleanup-source"; receiptPath |] ->
    if builder.Database <> "fogell" then failwith "source cleanup requires original owned database"
    use document = JsonDocument.Parse(File.ReadAllText receiptPath)
    let root = document.RootElement
    if store.CurrentRestoreEpoch().Value <> root.GetProperty("epoch").GetInt64() then
        failwith "original source authority epoch changed"
    let org = OrganizationId(root.GetProperty("organization").GetGuid())
    let attempt = AttemptId(root.GetProperty("attempt").GetGuid())
    let fence = Fence(root.GetProperty("fence").GetInt64())
    if not (store.RequireReconciliation(org, attempt, fence, owner, "luigi_recovery_probe_complete")) then
        failwith "source probe reconciliation fence refused"
    match store.AttemptState(org, attempt) with
    | Some("reconciliation_required", _, _) -> ()
    | _ -> failwith "source probe did not retain reconciliation evidence"
    printfn "{\"passed\":true,\"source_probe_state\":\"reconciliation_required\",\"terminal_success_fabricated\":false}"
| _ -> failwith "usage: seed|check|cleanup-source RECEIPT"
