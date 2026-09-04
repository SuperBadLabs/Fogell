namespace Fogell.Controller.Host

open System
open System.IO

/// FG-026b. The closed world of controller-managed external-effect producers.
///
/// Every producer that the controller itself drives against a destination
/// outside its own durable state is a case here and nowhere else. Adding a
/// case without teaching `EffectProducer.name` and `EffectDispatch` about it is
/// a compile error (FS0025 is an error in Directory.Build.props); leaving it
/// out of `EffectProducer.all` is caught by the registry test and the source
/// audit. A producer that is not a case cannot reach the Store ledger, because
/// the dispatch path is the only caller of `PrepareEffect`/`AdvanceEffect`.
[<RequireQualifiedAccess>]
type EffectProducer =
    /// The destination simulator: one receipt file per executed attempt,
    /// written to an operator-configured directory outside the state root.
    | FileDropReceipt

/// The crash window a configured simulator kill fires in. Honoured only when
/// the file-drop simulator is configured; see EffectProducerConfig.
[<RequireQualifiedAccess>]
type EffectKillWindow =
    | AfterPrepare
    | AfterInvoke
    | AfterApply
    | AfterConfirm

/// Which producers are enabled for this controller and whether the crash-window
/// proof's kill hook is armed. Production runs with every producer disabled
/// unless the operator names a destination.
type EffectProducerConfig =
    { FileDropRoot: string option
      KillAt: EffectKillWindow option }

module EffectProducer =
    /// The hand-maintained registry. Order is dispatch order.
    let all = [ EffectProducer.FileDropReceipt ]

    /// Stable wire and key prefix per producer. Exhaustive by construction.
    let name producer =
        match producer with
        | EffectProducer.FileDropReceipt -> "file-drop-receipt"

    /// The attempt-scoped ledger key: "<producer>:<identity>". The Store binds
    /// it to (organization, attempt), so a retry child with a new attempt id
    /// has a fresh identity for the same producer and destination.
    let effectKey producer (identity: string) = $"{name producer}:{identity}"

module EffectProducerConfig =
    let disabled = { FileDropRoot = None; KillAt = None }

    let killWindowNames =
        [ "prepare", EffectKillWindow.AfterPrepare
          "invoke", EffectKillWindow.AfterInvoke
          "apply", EffectKillWindow.AfterApply
          "confirm", EffectKillWindow.AfterConfirm ]

    let parseKillWindow (raw: string) =
        match killWindowNames |> List.tryFind (fun (spelling, _) -> spelling = raw) with
        | Some(_, window) -> Ok window
        | None -> Error "FOGELL_EFFECT_KILL_AT must be one of prepare, invoke, apply, confirm"

    let private withTrailingSeparator (path: string) =
        if path.EndsWith(Path.DirectorySeparatorChar) then path
        else path + string Path.DirectorySeparatorChar

    let private probeWritable (directory: string) =
        let probePath = Path.Combine(directory, $".fogell-effect-probe-{Guid.NewGuid():N}.tmp")

        try
            let options =
                FileStreamOptions(
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.DeleteOnClose,
                    UnixCreateMode = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

            use probe = File.Open(probePath, options)
            probe.WriteByte 0uy
            true
        with _ ->
            false

    /// The simulator destination must be an absolute, existing, writable
    /// directory that is disjoint from the controller state root: a receipt is
    /// an external effect only if it lives outside the state the controller
    /// restores.
    let validateFileDropRoot (stateRoot: string) (raw: string) =
        if not (Path.IsPathFullyQualified raw) then
            Error "FOGELL_EFFECT_FILE_DROP_ROOT must be absolute"
        else
            let root = Path.GetFullPath raw

            if not (Directory.Exists root) then
                Error "FOGELL_EFFECT_FILE_DROP_ROOT must name an existing directory"
            else
                let rootKey = withTrailingSeparator root
                let stateKey = withTrailingSeparator (Path.GetFullPath stateRoot)

                if rootKey.StartsWith(stateKey, StringComparison.Ordinal)
                   || stateKey.StartsWith(rootKey, StringComparison.Ordinal) then
                    Error "FOGELL_EFFECT_FILE_DROP_ROOT must be disjoint from FOGELL_STATE_ROOT"
                elif not (probeWritable root) then
                    Error "FOGELL_EFFECT_FILE_DROP_ROOT is not writable by the service identity"
                else
                    Ok root

    /// Reads the two optional variables. Both absent is the production default;
    /// a kill hook without a simulator destination is refused because nothing
    /// but the simulator may ever kill the controller.
    let loadFromEnvironment (stateRoot: string) : Result<EffectProducerConfig, string> =
        let optional name =
            match Environment.GetEnvironmentVariable name with
            | value when String.IsNullOrWhiteSpace value -> None
            | value -> Some value

        let root =
            match optional "FOGELL_EFFECT_FILE_DROP_ROOT" with
            | None -> Ok None
            | Some raw -> validateFileDropRoot stateRoot raw |> Result.map Some

        let kill =
            match optional "FOGELL_EFFECT_KILL_AT" with
            | None -> Ok None
            | Some raw -> parseKillWindow raw |> Result.map Some

        match root, kill with
        | Error error, _ -> Error error
        | _, Error error -> Error error
        | Ok None, Ok(Some _) ->
            Error "FOGELL_EFFECT_KILL_AT requires FOGELL_EFFECT_FILE_DROP_ROOT: a kill hook without a simulator destination is refused"
        | Ok root, Ok kill -> Ok { FileDropRoot = root; KillAt = kill }
