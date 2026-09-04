namespace Fogell.Controller.Host

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
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

/// One producer's invocation against its destination. `Invoke` must be
/// idempotent-or-ambiguous — it may find its effect already present and say
/// so, but it never retries towards success on its own. `Confirm` reads
/// destination evidence only and never changes the destination.
type EffectInvocation =
    { Producer: EffectProducer
      Identity: string
      Payload: byte array
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
    /// Authority or payload refusal at preparation. Nothing was invoked.
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

    let private write (path: string) (bytes: byte array) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

        if File.Exists path then
            // The destination is idempotent per attempt: a receipt that already
            // exists is this connector's contract, and Confirm decides whether
            // its bytes are the ones this attempt meant to leave.
            Ok()
        else
            let temporary = path + $".{Guid.NewGuid():N}.tmp"
            File.WriteAllBytes(temporary, bytes)

            try
                File.Move(temporary, path, false)
                Ok()
            with :? IOException when File.Exists path ->
                (try File.Delete temporary with _ -> ())
                Ok()

    let private read (path: string) (bytes: byte array) =
        if File.Exists path then
            Ok(CryptographicOperations.FixedTimeEquals(File.ReadAllBytes path, bytes))
        else
            Ok false

    let invocation (root: string) (claim: ExecutionClaim) (terminal: BuildStatus) : EffectInvocation =
        let path = receiptPath root claim
        let bytes = payload claim terminal

        { Producer = EffectProducer.FileDropReceipt
          Identity = identity claim
          Payload = bytes
          Invoke = fun () -> write path bytes
          Confirm = fun () -> read path bytes }

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
