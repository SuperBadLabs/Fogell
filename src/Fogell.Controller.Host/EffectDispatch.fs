namespace Fogell.Controller.Host

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open Microsoft.Win32.SafeHandles
open Fogell.Domain
open Fogell.Store

/// FG-026b. The attempt authority an effect runs under: the worker's owner
/// identity plus the fence it was claimed with. The Store adds the live lease
/// and the current restore epoch at every prepare and advance.
type EffectAuthority =
    { Organization: OrganizationId
      Attempt: AttemptId
      Fence: Fence
      Owner: string }

module EffectAuthority =
    let ofClaim (owner: string) (claim: ExecutionClaim) =
        { Organization = claim.OrganizationId
          Attempt = claim.AttemptId
          Fence = claim.Fence
          Owner = owner }

/// One producer's invocation against its destination. `Destination` says
/// whether the pinned destination is still the one configured (checked before
/// preparation and again inside `Invoke`; it never creates or repairs it).
/// `Invoke` must be idempotent-or-ambiguous — it may find its effect already
/// present and say so, but it never retries towards success on its own.
/// `Confirm` reads destination evidence only and never changes the destination.
type EffectInvocation =
    { Producer: EffectProducer
      Identity: string
      Payload: byte array
      Destination: unit -> Result<unit, string>
      Invoke: unit -> Result<unit, string>
      Confirm: unit -> Result<bool, string> }

/// Seams between the four ledger windows. Production passes
/// `EffectDispatch.noHooks` unless the simulator's kill window is configured;
/// the in-process crash tests abort through them.
type DispatchHooks =
    { AfterPrepare: unit -> unit
      AfterInvoke: unit -> unit
      AfterApply: unit -> unit
      AfterConfirm: unit -> unit }

[<RequireQualifiedAccess>]
type DispatchOutcome =
    /// The ledger reached confirmed. `replay` is true when it already had.
    | Confirmed of replay: bool
    /// Authority, payload or destination refusal before preparation completed.
    /// Nothing was invoked.
    | Refused of string
    /// The effect may have happened. The row stays prepared or applied for the
    /// bounded reconciliation trigger to classify; nothing re-invokes it.
    | Uncertain of string

module DispatchOutcome =
    let isConfirmed outcome =
        match outcome with
        | DispatchOutcome.Confirmed _ -> true
        | DispatchOutcome.Refused _
        | DispatchOutcome.Uncertain _ -> false

    let describe outcome =
        match outcome with
        | DispatchOutcome.Confirmed true -> "confirmed (replay)"
        | DispatchOutcome.Confirmed false -> "confirmed"
        | DispatchOutcome.Refused reason -> $"refused: {reason}"
        | DispatchOutcome.Uncertain reason -> $"uncertain: {reason}"

/// The destination simulator: one receipt file per executed attempt under an
/// operator-configured root outside the controller state root. The receipt is
/// the executed attempt's journal terminal, not the published build status;
/// PublishTerminal may still arbitrate a raced cancellation into Aborted.
module FileDropReceipt =
    let identity (claim: ExecutionClaim) = claim.AttemptId.Value.ToString "N"

    let receiptPath (root: string) (claim: ExecutionClaim) =
        Path.Combine(root, claim.OrganizationId.Value.ToString "N", identity claim + ".receipt")

    let statusName status =
        match status with
        | NotBuilt -> "NotBuilt"
        | Success -> "Success"
        | Unstable -> "Unstable"
        | Failure -> "Failure"
        | Aborted -> "Aborted"

    /// Canonical UTF-8 JSON with a fixed field order. Every value is a UUID,
    /// an integer, a hex digest or a status name, so no escaping is needed and
    /// the bytes are reproducible for the digest.
    let payload (claim: ExecutionClaim) (terminal: BuildStatus) =
        let text =
            String.concat
                ""
                [ "{\"build\":\""
                  claim.BuildId.Value.ToString()
                  "\",\"attempt\":\""
                  claim.AttemptId.Value.ToString()
                  "\",\"fence\":"
                  string claim.Fence.Value
                  ",\"pipeline_sha256\":\""
                  claim.PipelineSha256
                  "\",\"journal_terminal\":\""
                  statusName terminal
                  "\"}" ]

        Text.Encoding.UTF8.GetBytes text

    // ---- descriptor-bound destination I/O ------------------------------------
    // Codex on #424 (rounds 3 and 4). All destination I/O goes through
    // DestinationDescriptor: the root is opened once with O_DIRECTORY|O_NOFOLLOW,
    // the marker and the receipt are opened O_NONBLOCK relative to it and must
    // be regular files with one link before a byte is read, the organization
    // directory is mkdirat'ed non-recursively and fsynced into the root, the
    // temp file is an O_EXCL name of this process's choosing, published with
    // RENAME_NOREPLACE and fsynced into the organization directory, and the
    // marker is re-read through the root descriptor after the rename.

    [<Literal>]
    let private ReceiptFileMode = 0o600

    [<Literal>]
    let private OrganizationDirectoryMode = 0o755

    /// The root is pinned by the operator-created marker file that startup
    /// validated. An unmounted or replaced destination has no marker, and a
    /// receipt must never be created on whatever directory took its place.
    let pinned (root: string) = DestinationDescriptor.withPinnedRoot root (fun _ _ -> Ok())

    /// Only the per-organization directory is ever created, relative to the
    /// root descriptor and non-recursively; EEXIST is the idempotent case. The
    /// root is fsynced after the attempt so the new entry is durable before
    /// anything is written under it.
    let private openOrganizationDirectory
        (trace: string -> unit)
        (table: LinuxOpenFlags.Table)
        (rootHandle: SafeFileHandle)
        (name: string)
        =
        let made = DestinationDescriptor.mkdirAt rootHandle name OrganizationDirectoryMode

        if made < 0 && DestinationDescriptor.errno () <> DestinationDescriptor.EEXIST then
            Error $"cannot create the organization directory in the pinned destination (errno {DestinationDescriptor.errno ()})"
        else
            match DestinationDescriptor.sync rootHandle with
            | Error error -> Error $"cannot make the organization directory durable in the pinned destination: {error}"
            | Ok() ->
                trace "root-fsynced"
                let fd = DestinationDescriptor.openAt rootHandle name (DestinationDescriptor.directoryFlags table) 0

                if fd < 0 then
                    Error $"cannot open the organization directory in the pinned destination (errno {DestinationDescriptor.errno ()})"
                else
                    Ok(DestinationDescriptor.owned fd)

    /// Codex #424 round 8: the temp file's lifetime is one try/finally. On any
    /// exit that did not rename it — a refused create is nothing to clean, but a
    /// write or fsync that throws (ENOSPC, an I/O error), a rename refusal, and
    /// the EEXIST loser all leave through the same finally — the process's own
    /// O_EXCL name is unlinked through the organization directory descriptor
    /// (never anything else, never a directory), and an exception becomes the
    /// Uncertain reason instead of propagating. Repeated terminal attempts
    /// during a destination outage therefore leave no `.tmp` behind.
    let private writeWith
        (afterPin: unit -> unit)
        (trace: string -> unit)
        (writer: Stream -> byte array -> unit)
        (root: string)
        (organization: string)
        (fileName: string)
        (bytes: byte array)
        =
        DestinationDescriptor.withPinnedRoot root (fun table rootHandle ->
            afterPin ()

            match openOrganizationDirectory trace table rootHandle organization with
            | Error error -> Error error
            | Ok directoryHandle ->
                use directoryHandle = directoryHandle
                let existing = DestinationDescriptor.openAt directoryHandle fileName (DestinationDescriptor.fileReadFlags table) 0

                let written =
                    if existing >= 0 then
                        // The destination is idempotent per attempt: a receipt
                        // that already exists is this connector's contract, and
                        // Confirm decides whether its bytes are the ones this
                        // attempt meant to leave. Whatever it is (a FIFO, a
                        // directory, a link) it is only ever judged, never read
                        // here and never replaced.
                        (DestinationDescriptor.owned existing).Dispose()
                        trace "existing"
                        Ok()
                    else
                        let temporary = fileName + $".{Guid.NewGuid():N}.tmp"

                        let fd =
                            DestinationDescriptor.openAt
                                directoryHandle
                                temporary
                                (DestinationDescriptor.OpenWriteOnly
                                 ||| DestinationDescriptor.OpenCreate
                                 ||| DestinationDescriptor.OpenExclusive
                                 ||| table.NoFollow
                                 ||| DestinationDescriptor.OpenCloseOnExec)
                                ReceiptFileMode

                        if fd < 0 then
                            // Nothing was created; nothing to clean.
                            Error $"cannot create the receipt in the pinned destination (errno {DestinationDescriptor.errno ()})"
                        else
                            let mutable renamed = false

                            let published =
                                try
                                    try
                                        do
                                            use stream = new FileStream(DestinationDescriptor.owned fd, FileAccess.Write, 1, false)
                                            writer stream bytes
                                            stream.Flush true

                                        trace "temp-fsynced"

                                        if DestinationDescriptor.renameAt2 directoryHandle temporary fileName DestinationDescriptor.RenameNoReplace >= 0 then
                                            renamed <- true
                                            trace "renamed"
                                            Ok()
                                        else
                                            let error = DestinationDescriptor.errno ()

                                            if error = DestinationDescriptor.EEXIST then
                                                // Something now holds the receipt name: a
                                                // concurrent writer of the same attempt, or
                                                // a planted entry. Confirm judges it.
                                                Ok()
                                            else
                                                Error $"cannot publish the receipt in the pinned destination (errno {error})"
                                    with ex ->
                                        // ENOSPC, EIO, a closed descriptor: the effect
                                        // may or may not have reached the destination;
                                        // the row stays where it is for the trigger.
                                        Error $"receipt write failed in the pinned destination: {ex.Message}"
                                finally
                                    if not renamed then
                                        // Only this process's own O_EXCL temp name is
                                        // ever unlinked, through the directory
                                        // descriptor, and never a directory.
                                        DestinationDescriptor.unlinkAt directoryHandle temporary |> ignore
                                        trace "temp-unlinked"

                            match published with
                            | Error error -> Error error
                            | Ok() ->
                                // The directory entry is durable before the ledger
                                // may record applied or confirmed.
                                match DestinationDescriptor.sync directoryHandle with
                                | Error error -> Error $"cannot make the receipt durable in the pinned destination: {error}"
                                | Ok() ->
                                    trace "organization-fsynced"
                                    Ok()

                // The marker is re-read through the same descriptor after the
                // rename: a root that was replaced meanwhile is reported as
                // ambiguous rather than as a confirmed effect.
                match written with
                | Error error -> Error error
                | Ok() ->
                    match DestinationDescriptor.markerPinned table rootHandle with
                    | Error error -> Error $"destination root is not pinned: {error}"
                    | Ok() -> Ok())

    let private writeBytes (stream: Stream) (bytes: byte array) = stream.Write(bytes, 0, bytes.Length)

    /// Evidence is a regular, single-link receipt of exactly the payload's
    /// length, opened non-blocking through the descriptors and read through a
    /// bounded stream after the statx. Any other type, length, link count,
    /// a short read, or an unpinned root is no evidence (Ok false); nothing
    /// here allocates more than the payload or blocks on a special file.
    let private read (root: string) (organization: string) (fileName: string) (bytes: byte array) =
        let evidence =
            DestinationDescriptor.withPinnedRoot root (fun table rootHandle ->
                let directory = DestinationDescriptor.openAt rootHandle organization (DestinationDescriptor.directoryFlags table) 0

                if directory < 0 then
                    Ok false
                else
                    use directoryHandle = DestinationDescriptor.owned directory
                    let fd = DestinationDescriptor.openAt directoryHandle fileName (DestinationDescriptor.fileReadFlags table) 0

                    if fd < 0 then
                        Ok false
                    else
                        use handle = DestinationDescriptor.owned fd

                        match DestinationDescriptor.regularFile handle with
                        | Ok file when file.Links = 1u && file.Size = int64 bytes.Length ->
                            match DestinationDescriptor.readBounded handle (int64 bytes.Length) with
                            | Ok content -> Ok(CryptographicOperations.FixedTimeEquals(content, bytes))
                            | Error _ -> Ok false
                        | Ok _
                        | Error _ -> Ok false)

        match evidence with
        | Ok present -> Ok present
        | Error _ -> Ok false

    let private organizationName (claim: ExecutionClaim) = claim.OrganizationId.Value.ToString "N"
    let private receiptFileName (claim: ExecutionClaim) = identity claim + ".receipt"

    /// The in-process test seam: `afterPin` runs after the root descriptor and
    /// its marker were verified and before anything is created; `trace`
    /// receives the write's durability steps in order.
    let internal invocationWithWriter
        (afterPin: unit -> unit)
        (trace: string -> unit)
        (writer: Stream -> byte array -> unit)
        (root: string)
        (claim: ExecutionClaim)
        (terminal: BuildStatus)
        : EffectInvocation =
        let organization = organizationName claim
        let fileName = receiptFileName claim
        let bytes = payload claim terminal

        { Producer = EffectProducer.FileDropReceipt
          Identity = identity claim
          Payload = bytes
          Destination = fun () -> pinned root
          Invoke = fun () -> writeWith afterPin trace writer root organization fileName bytes
          Confirm = fun () -> read root organization fileName bytes }

    /// The in-process seams without an injected writer.
    let internal invocationWith (afterPin: unit -> unit) (trace: string -> unit) =
        invocationWithWriter afterPin trace writeBytes

    let invocation (root: string) (claim: ExecutionClaim) (terminal: BuildStatus) : EffectInvocation =
        invocationWith ignore ignore root claim terminal

module EffectDispatch =
    let noHooks =
        { AfterPrepare = ignore
          AfterInvoke = ignore
          AfterApply = ignore
          AfterConfirm = ignore }

    /// The simulator's crash-window hook. This is the only place a kill can
    /// originate, and the configuration refuses it without a simulator root.
    let killHooks (window: EffectKillWindow option) =
        match window with
        | None -> noHooks
        | Some window ->
            let kill () = Process.GetCurrentProcess().Kill()

            match window with
            | EffectKillWindow.AfterPrepare -> { noHooks with AfterPrepare = kill }
            | EffectKillWindow.AfterInvoke -> { noHooks with AfterInvoke = kill }
            | EffectKillWindow.AfterApply -> { noHooks with AfterApply = kill }
            | EffectKillWindow.AfterConfirm -> { noHooks with AfterConfirm = kill }

    let private guarded (action: unit -> Result<'a, string>) =
        try
            action ()
        with ex ->
            Error ex.Message

    /// The single dispatch path. This is the only function under src/ that
    /// calls Store.PrepareEffect and Store.AdvanceEffect (the source audit
    /// enforces that). Order: prepare -> invoke -> record applied -> read
    /// destination evidence -> record confirmed. A refusal at preparation
    /// invokes nothing; any failure after invocation leaves the row where it is
    /// and reports Uncertain — nothing here retries an invocation.
    let run (store: Store) (authority: EffectAuthority) (hooks: DispatchHooks) (invocation: EffectInvocation) =
        let key = EffectProducer.effectKey invocation.Producer invocation.Identity

        let advance step =
            store.AdvanceEffect(
                authority.Organization,
                authority.Attempt,
                authority.Fence,
                authority.Owner,
                key,
                invocation.Payload,
                step)

        let confirmFromEvidence () =
            match guarded invocation.Confirm with
            | Ok true ->
                match advance RecordConfirmed with
                | Ok outcome ->
                    hooks.AfterConfirm()
                    DispatchOutcome.Confirmed outcome.WasReplay
                | Error error -> DispatchOutcome.Uncertain $"confirmation not recorded: {error}"
            | Ok false -> DispatchOutcome.Uncertain "destination evidence absent after invocation"
            | Error error -> DispatchOutcome.Uncertain $"destination evidence unreadable: {error}"

        // A destination that is no longer the pinned one refuses before any
        // ledger row exists: there is nothing to prepare against.
        match guarded invocation.Destination with
        | Error error -> DispatchOutcome.Refused $"destination refused: {error}"
        | Ok() ->

            match
                store.PrepareEffect(
                    authority.Organization,
                    authority.Attempt,
                    authority.Fence,
                    authority.Owner,
                    key,
                    invocation.Payload)
            with
            | Error error -> DispatchOutcome.Refused error
            | Ok outcome ->
                match outcome.Checkpoint.State with
                | EffectConfirmed -> DispatchOutcome.Confirmed true
                | EffectUncertain ->
                    DispatchOutcome.Uncertain "checkpoint is already uncertain and awaits operator reconciliation"
                | EffectApplied ->
                    // Applied means the invocation completed once. Re-read the
                    // evidence; never invoke again.
                    confirmFromEvidence ()
                | EffectPrepared ->
                    hooks.AfterPrepare()

                    match guarded invocation.Invoke with
                    | Error error -> DispatchOutcome.Uncertain $"invocation failed after preparation: {error}"
                    | Ok() ->
                        hooks.AfterInvoke()

                        match advance RecordApplied with
                        | Error error -> DispatchOutcome.Uncertain $"application not recorded: {error}"
                        | Ok _ ->
                            hooks.AfterApply()
                            confirmFromEvidence ()

    /// Every registered producer that the configuration enables, in registry
    /// order. The match is exhaustive over EffectProducer, so a new case must
    /// be routed here before the host compiles.
    let registered (config: EffectProducerConfig) (claim: ExecutionClaim) (terminal: BuildStatus) =
        EffectProducer.all
        |> List.choose (fun producer ->
            match producer with
            | EffectProducer.FileDropReceipt ->
                config.FileDropRoot
                |> Option.map (fun root -> FileDropReceipt.invocation root claim terminal))

    /// Dispatches every enabled producer for one executed attempt. With no
    /// producer enabled this returns [] and makes no Store call at all, so the
    /// terminal path of a controller without a configured destination is
    /// unchanged.
    let runRegistered
        (store: Store)
        (authority: EffectAuthority)
        (hooks: DispatchHooks)
        (config: EffectProducerConfig)
        (claim: ExecutionClaim)
        (terminal: BuildStatus)
        : (EffectProducer * DispatchOutcome) list =
        registered config claim terminal
        |> List.map (fun invocation -> invocation.Producer, run store authority hooks invocation)
