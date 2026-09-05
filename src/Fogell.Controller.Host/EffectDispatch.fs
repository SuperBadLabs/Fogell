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
    // Codex on #424 (EffectDispatch.fs:129, :161). The configured path string is
    // resolved exactly once per operation, to open the root with
    // O_DIRECTORY|O_NOFOLLOW; everything after that — the marker check, the
    // per-organization directory, the temp file, the rename and the evidence
    // read — happens relative to that descriptor. If the mount vanishes after
    // the pin, mkdirat/openat on the dead descriptor fail (ENOENT/ESTALE) and
    // nothing is created at the configured path on whatever filesystem is
    // underneath. Evidence is read through a bounded stream whose length is
    // checked first, so a pre-written oversized receipt costs a stat, not an
    // allocation.

    [<Literal>]
    let private OpenReadOnly = 0

    [<Literal>]
    let private OpenWriteOnly = 1

    [<Literal>]
    let private OpenCreate = 0x40

    [<Literal>]
    let private OpenExclusive = 0x80

    [<Literal>]
    let private OpenCloseOnExec = 0x80000

    [<Literal>]
    let private RenameNoReplace = 1u

    [<Literal>]
    let private EEXIST = 17

    [<Literal>]
    let private ReceiptFileMode = 0o600

    [<Literal>]
    let private OrganizationDirectoryMode = 0o755

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openPath(string path, int flags)

    [<DllImport("libc", EntryPoint = "openat", SetLastError = true)>]
    extern int private openAt(int directory, string name, int flags, int mode)

    [<DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)>]
    extern int private mkdirAt(int directory, string name, int mode)

    [<DllImport("libc", EntryPoint = "renameat2", SetLastError = true)>]
    extern int private renameAt2(int oldDirectory, string oldName, int newDirectory, string newName, uint32 flags)

    [<DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)>]
    extern int private unlinkAt(int directory, string name, int flags)

    let private errno () = Marshal.GetLastPInvokeError()
    let private owned (fd: int) = new SafeFileHandle(nativeint fd, true)
    let private descriptor (handle: SafeFileHandle) = int (handle.DangerousGetHandle())

    let private directoryFlags (table: LinuxOpenFlags.Table) =
        OpenReadOnly ||| table.Directory ||| table.NoFollow ||| OpenCloseOnExec

    let private fileReadFlags (table: LinuxOpenFlags.Table) =
        OpenReadOnly ||| table.NoFollow ||| OpenCloseOnExec

    /// The one place the configured path string is resolved.
    let private openRoot (table: LinuxOpenFlags.Table) (root: string) =
        let fd = openPath (root, directoryFlags table)

        if fd < 0 then
            Error $"destination root is not pinned: cannot open {root} as a directory without following links (errno {errno ()})"
        else
            Ok(owned fd)

    /// The marker seen through the root descriptor, never through the path.
    let private markerPresent (table: LinuxOpenFlags.Table) (rootHandle: SafeFileHandle) =
        let fd = openAt (descriptor rootHandle, EffectProducerConfig.fileDropRootMarker, fileReadFlags table, 0)

        if fd < 0 then
            Error $"destination root is not pinned: its {EffectProducerConfig.fileDropRootMarker} marker is absent (errno {errno ()})"
        else
            (owned fd).Dispose()
            Ok()

    let private withPinnedRoot (root: string) (body: LinuxOpenFlags.Table -> SafeFileHandle -> Result<'a, string>) =
        match LinuxOpenFlags.current with
        | Error error -> Error $"destination root is not pinned: {error}"
        | Ok table ->
            match openRoot table root with
            | Error error -> Error error
            | Ok rootHandle ->
                use rootHandle = rootHandle

                match markerPresent table rootHandle with
                | Error error -> Error error
                | Ok() -> body table rootHandle

    /// The root is pinned by the operator-created marker file that startup
    /// validated. An unmounted or replaced destination has no marker, and a
    /// receipt must never be created on whatever directory took its place.
    let pinned (root: string) = withPinnedRoot root (fun _ _ -> Ok())

    /// Only the per-organization directory is ever created, relative to the
    /// root descriptor and non-recursively; EEXIST is the idempotent case.
    let private openOrganizationDirectory (table: LinuxOpenFlags.Table) (rootHandle: SafeFileHandle) (name: string) =
        let made = mkdirAt (descriptor rootHandle, name, OrganizationDirectoryMode)

        if made < 0 && errno () <> EEXIST then
            Error $"cannot create the organization directory in the pinned destination (errno {errno ()})"
        else
            let fd = openAt (descriptor rootHandle, name, directoryFlags table, 0)

            if fd < 0 then
                Error $"cannot open the organization directory in the pinned destination (errno {errno ()})"
            else
                Ok(owned fd)

    let private writeWith (afterPin: unit -> unit) (root: string) (organization: string) (fileName: string) (bytes: byte array) =
        withPinnedRoot root (fun table rootHandle ->
            afterPin ()

            match openOrganizationDirectory table rootHandle organization with
            | Error error -> Error error
            | Ok directoryHandle ->
                use directoryHandle = directoryHandle
                let directory = descriptor directoryHandle
                let existing = openAt (directory, fileName, fileReadFlags table, 0)

                let written =
                    if existing >= 0 then
                        // The destination is idempotent per attempt: a receipt
                        // that already exists is this connector's contract, and
                        // Confirm decides whether its bytes are the ones this
                        // attempt meant to leave.
                        (owned existing).Dispose()
                        Ok()
                    else
                        let temporary = fileName + $".{Guid.NewGuid():N}.tmp"

                        let fd =
                            openAt (
                                directory,
                                temporary,
                                OpenWriteOnly ||| OpenCreate ||| OpenExclusive ||| table.NoFollow ||| OpenCloseOnExec,
                                ReceiptFileMode
                            )

                        if fd < 0 then
                            Error $"cannot create the receipt in the pinned destination (errno {errno ()})"
                        else
                            do
                                use stream = new FileStream(owned fd, FileAccess.Write, 1, false)
                                stream.Write(bytes, 0, bytes.Length)
                                stream.Flush true

                            if renameAt2 (directory, temporary, directory, fileName, RenameNoReplace) >= 0 then
                                Ok()
                            else
                                let error = errno ()
                                unlinkAt (directory, temporary, 0) |> ignore

                                if error = EEXIST then
                                    // A concurrent writer of the same attempt won
                                    // the rename; that receipt is the idempotent
                                    // destination and Confirm judges its bytes.
                                    Ok()
                                else
                                    Error $"cannot publish the receipt in the pinned destination (errno {error})"

                // The marker is re-read through the same descriptor after the
                // rename: a root that was replaced meanwhile is reported as
                // ambiguous rather than as a confirmed effect.
                match written with
                | Error error -> Error error
                | Ok() -> markerPresent table rootHandle)

    let private write root organization fileName bytes =
        writeWith ignore root organization fileName bytes

    /// Evidence is a byte-exact receipt of exactly the payload's length, read
    /// through a bounded stream after the length is checked on the open
    /// descriptor. Any other length, a short read, or an unpinned root is no
    /// evidence (Ok false); nothing here allocates more than the payload.
    let private read (root: string) (organization: string) (fileName: string) (bytes: byte array) =
        let evidence =
            withPinnedRoot root (fun table rootHandle ->
                let directory = openAt (descriptor rootHandle, organization, directoryFlags table, 0)

                if directory < 0 then
                    Ok false
                else
                    use directoryHandle = owned directory
                    let fd = openAt (descriptor directoryHandle, fileName, fileReadFlags table, 0)

                    if fd < 0 then
                        Ok false
                    else
                        use stream = new FileStream(owned fd, FileAccess.Read, 1, false)

                        if stream.Length <> int64 bytes.Length then
                            Ok false
                        else
                            let buffer = Array.zeroCreate<byte> bytes.Length

                            try
                                stream.ReadExactly(buffer, 0, buffer.Length)
                                // A byte past the expected length is not the receipt.
                                Ok(stream.ReadByte() = -1 && CryptographicOperations.FixedTimeEquals(buffer, bytes))
                            with :? EndOfStreamException ->
                                Ok false)

        match evidence with
        | Ok present -> Ok present
        | Error _ -> Ok false

    let private organizationName (claim: ExecutionClaim) = claim.OrganizationId.Value.ToString "N"
    let private receiptFileName (claim: ExecutionClaim) = identity claim + ".receipt"

    /// The in-process test seam: `afterPin` runs after the root descriptor and
    /// its marker were verified and before anything is created.
    let internal invocationWith (afterPin: unit -> unit) (root: string) (claim: ExecutionClaim) (terminal: BuildStatus) : EffectInvocation =
        let organization = organizationName claim
        let fileName = receiptFileName claim
        let bytes = payload claim terminal

        { Producer = EffectProducer.FileDropReceipt
          Identity = identity claim
          Payload = bytes
          Destination = fun () -> pinned root
          Invoke = fun () -> writeWith afterPin root organization fileName bytes
          Confirm = fun () -> read root organization fileName bytes }

    let invocation (root: string) (claim: ExecutionClaim) (terminal: BuildStatus) : EffectInvocation =
        invocationWith ignore root claim terminal

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
