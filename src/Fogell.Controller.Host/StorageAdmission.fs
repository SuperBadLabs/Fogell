namespace Fogell.Controller.Host

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open Fogell.Domain

module private StorageAdmissionNative =
    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int openDirectory(string path, int flags)

    [<DllImport("libc", EntryPoint = "fsync", SetLastError = true)>]
    extern int syncDirectory(int descriptor)

    [<DllImport("libc", EntryPoint = "close")>]
    extern int closeDescriptor(int descriptor)


/// A single writer gate for an operator-provisioned bounded filesystem.
/// Free-space observations are admission guards, never byte reservations.
type StorageAdmission(stateRoot: string, policy: StoragePoolPolicy option) =
    let sync = obj ()
    let mutable lease: StoragePoolLease.PoolLease option = None
    let mutable identity: StoragePoolIdentity option = None
    let mutable active = false
    let mutable observation: StoragePoolObservation option = None
    let mutable lastDecision: string option = None
    let mutable disposed = false

    // Keep one bounded current decision on the controller filesystem. This is
    // deliberately outside the pool: ENOSPC must not erase the refusal reason.
    let persist decision =
        try
            if lease.IsSome && lastDecision <> Some decision then
                let target = Path.Combine(stateRoot, "storage-admission.json")
                let temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp"
                try
                    let bytes =
                        JsonSerializer.SerializeToUtf8Bytes(
                            {| pool_id = policy |> Option.map _.PoolId |> Option.defaultValue ""
                               schema = 1
                               decision = decision
                               available_bytes = observation |> Option.map _.AvailableBytes
                               available_inodes = observation |> Option.map _.AvailableInodes
                               observed_at = DateTimeOffset.UtcNow |})
                    use stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    stream.Write bytes
                    stream.Flush true
                    File.Move(temporary, target, true)
                    let flags = LinuxOpenFlags.current |> Result.defaultWith invalidOp
                    let fd = StorageAdmissionNative.openDirectory(stateRoot, flags.Directory ||| flags.NoFollow ||| 0x80000)
                    if fd < 0 then invalidOp "cannot open storage decision directory"
                    try
                        if StorageAdmissionNative.syncDirectory(fd) <> 0 then
                            invalidOp "cannot sync storage decision directory"
                    finally
                        StorageAdmissionNative.closeDescriptor(fd) |> ignore
                    lastDecision <- Some decision
                finally
                    if File.Exists temporary then File.Delete temporary
            Ok ()
        with _ ->
            lastDecision <- None
            Error "storage_decision_unwritable"

    let capacity (configured: StoragePoolPolicy) (observed: StoragePoolObservation) =
        if observed.AvailableBytes < configured.MinFreeBytes then Error "storage_pool_byte_pressure"
        elif observed.AvailableInodes < configured.MinFreeInodes then Error "storage_pool_inode_pressure"
        else Ok ()

    let unmanaged () =
        try
            if [ ".fogell-pool-id"; ".fogell-pool-state" ]
               |> List.exists (fun name -> Path.Exists(Path.Combine(stateRoot, "workspaces", name))) then
                Error "storage_pool_policy_required"
            else Ok ()
        with _ -> Error "storage_pool_configuration_unavailable"

    let check () =
        match policy with
        | None -> unmanaged ()
        | Some _ when disposed -> Error "storage_gate_closed"
        | Some configured ->
            // Acquire the exclusive lock even under pressure, so only its owner
            // publishes the durable decision. Zero guards here do not weaken
            // the filesystem size/identity checks; guards are applied below.
            StoragePool.probe stateRoot { configured with MinFreeBytes = 0UL; MinFreeInodes = 0UL }
            |> Result.bind (fun observed ->
                observation <- Some observed
                match identity with
                | Some pinned when pinned <> observed.Identity -> Error "storage_pool_replaced"
                | _ ->
                    let owned =
                        match lease with
                        | Some held -> Ok held
                        | None ->
                            StoragePoolLease.tryOpen stateRoot
                            |> Result.map (fun opened ->
                                lease <- Some opened
                                identity <- Some opened.Identity
                                opened)
                    owned
                    |> Result.bind (fun held ->
                        // Recheck against the actual locked directory, not a
                        // pathname observation made before acquiring its lock.
                        StoragePool.probePinned
                            stateRoot
                            { configured with MinFreeBytes = 0UL; MinFreeInodes = 0UL }
                            held.Identity
                        |> Result.bind (fun fresh ->
                            observation <- Some fresh
                            held.CheckIdle()
                            |> Result.bind (fun () -> capacity configured fresh))))

    let decide result =
        match result with
        | Ok () -> persist "ready"
        | Error reason ->
            match persist reason with
            | Ok () -> Error reason
            | Error failure -> Error failure

    member _.CheckStart() = lock sync (fun () -> if active then Error "storage_pool_busy" else check () |> decide)

    member _.Begin(attempt: string) =
        lock sync (fun () ->
            check () |> decide
            |> Result.bind (fun () ->
                match lease with
                | None -> Ok ()
                | Some held -> held.MarkActive attempt)
            |> Result.bind (fun () ->
                match persist "active" with
                | Ok () -> active <- policy.IsSome; Ok ()
                | Error reason ->
                    // No caller can launch before Begin succeeds. Compensate
                    // this known-unstarted failure without claiming readiness.
                    lease |> Option.iter (fun held -> held.Complete() |> ignore)
                    Error reason))

    member _.Complete() =
        lock sync (fun () ->
            match lease with
            | None -> ()
            | Some held ->
                match held.Complete() with
                | Ok () -> active <- false; persist "ready" |> ignore
                | Error reason -> active <- false; persist reason |> ignore)

    member _.Uncertain() =
        lock sync (fun () ->
            active <- false
            if policy.IsSome then persist "storage_pool_reconciliation_required" |> ignore)

    member _.Ready() =
        lock sync (fun () ->
            match policy with
            | None -> unmanaged () |> Result.isOk
            | Some configured ->
                if active then
                    match StoragePool.probe stateRoot configured with
                    | Ok observation -> identity = Some observation.Identity
                    | Error _ -> false
                else
                    check () |> decide |> Result.isOk)

    interface IDisposable with
        member _.Dispose() =
            lock sync (fun () ->
                disposed <- true
                lease |> Option.iter (fun held -> (held :> IDisposable).Dispose())
                lease <- None)
