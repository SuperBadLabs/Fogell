namespace Fogell.Controller.Host

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open Microsoft.Win32.SafeHandles
open Fogell.Domain

type ControllerConfig =
    { RuntimeDatabaseUrl: string
      MaintenanceDatabaseUrl: string
      ApiToken: string
      ListenUrl: string
      StateRoot: string
      RunHostPath: string
      SetsidPath: string
      TrustPool: string
      MaxPipelineBytes: int
      MaxLogChunks: int
      PollMilliseconds: int
      LeaseSeconds: int }

module ControllerConfig =

    [<Literal>]
    let internal maxApiTokenFileBytes = 4096

    [<Literal>]
    let private OpenReadOnly = 0

    [<Literal>]
    let private OpenNonBlocking = 0x800

    [<Literal>]
    let private OpenCloseOnExec = 0x80000

    [<Literal>]
    let private AtEmptyPath = 0x1000

    [<Literal>]
    let private AtNoAutomount = 0x800

    [<Literal>]
    let private StatxType = 0x1u

    [<Literal>]
    let private StatxMode = 0x2u

    [<Literal>]
    let private StatxUid = 0x8u

    [<Literal>]
    let private StatxSize = 0x200u

    [<Literal>]
    let private RegularFileType = 0x8000us

    [<Literal>]
    let private FileTypeMask = 0xF000us

    [<Literal>]
    let private PermissionMask = 0x0FFFus

    [<StructLayout(LayoutKind.Sequential, Size = 256)>]
    type internal LinuxStatx =
        struct
            val mutable Mask: uint32
            val mutable BlockSize: uint32
            val mutable Attributes: uint64
            val mutable LinkCount: uint32
            val mutable UserId: uint32
            val mutable GroupId: uint32
            val mutable Mode: uint16
            val mutable Spare0: uint16
            val mutable Inode: uint64
            val mutable Size: uint64
        end

    type internal TokenFileMetadata =
        { Mode: uint16
          Owner: uint32
          Size: uint64 }

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openFile(string path, int flags)

    [<DllImport("libc", SetLastError = true)>]
    extern int private statx(int directoryFileDescriptor, string path, int flags, uint32 mask, LinuxStatx& buffer)

    [<DllImport("libc")>]
    extern uint32 private geteuid()

    let internal tokenFileOpenFlags (table: LinuxOpenFlags.Table) =
        OpenReadOnly ||| OpenNonBlocking ||| table.NoFollow ||| OpenCloseOnExec

    let internal tokenFileMetadataFromStatx (status: LinuxStatx) =
        let required = StatxType ||| StatxMode ||| StatxUid ||| StatxSize

        if status.Mask &&& required <> required then
            Error "FOGELL_API_TOKEN_FILE metadata could not be read from the opened file"
        else
            Ok
                { Mode = status.Mode
                  Owner = status.UserId
                  Size = status.Size }

    let internal validateTokenFileMetadata effectiveUserId metadata =
        let permissions = metadata.Mode &&& PermissionMask

        if metadata.Mode &&& FileTypeMask <> RegularFileType then
            Error "FOGELL_API_TOKEN_FILE must name a regular non-symlink file"
        elif metadata.Owner <> effectiveUserId then
            Error "FOGELL_API_TOKEN_FILE must be owned by the service identity"
        elif permissions <> 0x0100us && permissions <> 0x0180us then
            Error "FOGELL_API_TOKEN_FILE mode must be 0400 or 0600"
        elif metadata.Size > uint64 maxApiTokenFileBytes then
            Error $"FOGELL_API_TOKEN_FILE must be at most {maxApiTokenFileBytes} bytes"
        else
            Ok()

    let private readBoundedTokenBytes (stream: FileStream) =
        let bytes = Array.zeroCreate<byte> (maxApiTokenFileBytes + 1)
        let mutable total = 0
        let mutable reading = true

        while reading && total < bytes.Length do
            let count = stream.Read(bytes, total, bytes.Length - total)

            if count = 0 then
                reading <- false
            else
                total <- total + count

        if total > maxApiTokenFileBytes then
            Error $"FOGELL_API_TOKEN_FILE must be at most {maxApiTokenFileBytes} bytes"
        else
            try
                // Preserve the historical acceptance of a UTF-8 BOM, but do
                // not let StreamReader's BOM detection admit UTF-16/32 or let
                // replacement fallback silently rewrite malformed bytes.
                let offset =
                    if total >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then 3 else 0

                let strictUtf8 = UTF8Encoding(false, true)
                Ok(strictUtf8.GetString(bytes, offset, total - offset))
            with _ ->
                Error "FOGELL_API_TOKEN_FILE could not be decoded"

    /// Open once with no-follow and classify the opened inode, then read only
    /// through that descriptor. The callbacks are internal test seams used to
    /// prove pathname replacement and post-metadata growth cannot bypass it.
    let internal readTokenFileSecurelyWith afterOpen afterMetadata path =
        if not (OperatingSystem.IsLinux()) then
            Error "FOGELL_API_TOKEN_FILE security requires Linux"
        else
            match LinuxOpenFlags.current with
            | Error reason -> Error $"FOGELL_API_TOKEN_FILE security is unavailable: {reason}"
            | Ok table ->
                let descriptor =
                    openFile (path, tokenFileOpenFlags table)

                if descriptor < 0 then
                    Error "FOGELL_API_TOKEN_FILE must name a readable regular non-symlink file"
                else
                    use handle = new SafeFileHandle(nativeint descriptor, true)

                    try
                        afterOpen descriptor
                        let mutable status = Unchecked.defaultof<LinuxStatx>
                        let required = StatxType ||| StatxMode ||| StatxUid ||| StatxSize

                        if statx (descriptor, "", AtEmptyPath ||| AtNoAutomount, required, &status) <> 0 then
                            Error "FOGELL_API_TOKEN_FILE metadata could not be read from the opened file"
                        else
                            match tokenFileMetadataFromStatx status with
                            | Error error -> Error error
                            | Ok metadata ->
                                match validateTokenFileMetadata (geteuid ()) metadata with
                                | Error error -> Error error
                                | Ok () ->
                                    afterMetadata ()
                                    use stream = new FileStream(handle, FileAccess.Read, 4096, false)
                                    readBoundedTokenBytes stream
                    with _ ->
                        Error "FOGELL_API_TOKEN_FILE could not be securely read"

    let internal readTokenFileSecurely path =
        readTokenFileSecurelyWith ignore ignore path

    [<Literal>]
    let internal trustedSetsidLauncher = "/usr/bin/setsid"

    [<Literal>]
    let private AtCurrentWorkingDirectory = -100

    [<Literal>]
    let private ExecuteAccess = 1

    [<Literal>]
    let private UseEffectiveIdentity = 0x200

    [<DllImport("libc", SetLastError = true)>]
    extern int private faccessat(int directoryFileDescriptor, string path, int mode, int flags)

    let internal isExecutableByServiceIdentity path =
        try
            OperatingSystem.IsLinux()
            && File.Exists path
            && faccessat(AtCurrentWorkingDirectory, path, ExecuteAccess, UseEffectiveIdentity) = 0
        with _ ->
            false

    let internal validateSetsidLauncher path =
        let fullPath = Path.GetFullPath path

        if isExecutableByServiceIdentity fullPath then
            Ok fullPath
        else
            Error "trusted setsid launcher does not name an executable file"

    let internal executionLaunchersReadyAt runHostPath setsidPath =
        isExecutableByServiceIdentity runHostPath
        && isExecutableByServiceIdentity setsidPath

    let internal executionLaunchersReady (config: ControllerConfig) =
        executionLaunchersReadyAt config.RunHostPath config.SetsidPath

    let private writeStateRootProbe probePath =
        let options =
            FileStreamOptions(
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = (FileOptions.DeleteOnClose ||| FileOptions.WriteThrough),
                UnixCreateMode = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

        use probe = File.Open(probePath, options)
        probe.WriteByte 0uy
        probe.Flush true

    let private probePathAbsent probePath =
        try
            use _probe = File.Open(probePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            false
        with
        | :? FileNotFoundException -> true
        | _ -> false

    let internal stateRootReadyAtWith writeProbe path =
        // Readiness must not recreate a missing configured root: doing so could
        // put durable controller state somewhere the operator did not restore.
        if not (Directory.Exists path) then
            false
        else
            let probePath =
                Path.Combine(path, $".fogell-readiness-{Guid.NewGuid():N}.tmp")

            let mutable written = false
            let mutable cleaned = false

            try
                try
                    writeProbe probePath
                    written <- true
                with _ ->
                    ()

                try
                    File.Delete probePath
                    cleaned <- probePathAbsent probePath
                with _ ->
                    cleaned <- false

                written && cleaned && Directory.Exists path
            with _ ->
                false

    let internal stateRootReadyAt path =
        stateRootReadyAtWith writeStateRootProbe path

    let internal stateRootReady (config: ControllerConfig) =
        stateRootReadyAt config.StateRoot

    [<Literal>]
    let internal stateRootProbeIntervalMilliseconds = 1000L

    type internal StateRootReadinessCache
        (
            probeIntervalMilliseconds: int64,
            monotonicMilliseconds: unit -> int64,
            probe: unit -> bool
        ) =
        do
            if probeIntervalMilliseconds <= 0L then
                invalidArg (nameof probeIntervalMilliseconds) "probe interval must be positive"

        let gate = obj ()
        let mutable lastProbeAt: int64 option = None
        let mutable cachedReady = false

        let runProbe now =
            let ready =
                try probe () with _ -> false

            lastProbeAt <- Some now
            cachedReady <- ready
            ready

        member _.Cached() =
            lock gate (fun () ->
                let now = monotonicMilliseconds ()

                match lastProbeAt with
                | None -> runProbe now
                | Some last when now < last || now - last >= probeIntervalMilliseconds ->
                    runProbe now
                | Some _ -> cachedReady)

        member _.Fresh() =
            lock gate (fun () -> runProbe (monotonicMilliseconds ()))

    let internal createStateRootReadinessCache (config: ControllerConfig) =
        StateRootReadinessCache(
            stateRootProbeIntervalMilliseconds,
            (fun () -> Environment.TickCount64),
            (fun () -> stateRootReady config))

    let internal prepareStateRoot path =
        try
            Directory.CreateDirectory path |> ignore

            if stateRootReadyAt path then
                Ok()
            else
                Error "FOGELL_STATE_ROOT cannot be created and written by the service identity"
        with _ ->
            Error "FOGELL_STATE_ROOT cannot be created and written by the service identity"

    let internal validateStateRoot (raw: string) =
        // Validate the operator-supplied value before normalization. GetFullPath
        // makes every relative path absolute by resolving it against the current
        // working directory, which would otherwise defeat the durability guard.
        if Path.IsPathFullyQualified raw then
            Ok(Path.GetFullPath raw)
        else
            Error "FOGELL_STATE_ROOT must be absolute"

    let internal validateTokenFilePath (raw: string) =
        if Path.IsPathFullyQualified raw then
            Ok(Path.GetFullPath raw)
        else
            Error "FOGELL_API_TOKEN_FILE must be absolute"

    let internal validateWorkerTiming pollMilliseconds leaseSeconds =
        // Renewal is targeted at one third of the lease. A control check can
        // occur immediately before that target and then sleep for one complete
        // poll, so cap the poll at one third as well. The next check is then no
        // later than two thirds of the lease, leaving its final third for
        // scheduling delay and the fenced renewal round trip.
        let poll = int64 pollMilliseconds
        let leaseMilliseconds = int64 leaseSeconds * 1000L

        if poll * 3L <= leaseMilliseconds then
            Ok()
        else
            Error
                $"FOGELL_WORKER_POLL_MS must be no more than one third of FOGELL_WORKER_LEASE_SECONDS ({leaseMilliseconds / 3L} ms for {leaseSeconds} s)"

    let private required name =
        match Environment.GetEnvironmentVariable name with
        | value when String.IsNullOrWhiteSpace value -> Error $"{name} is required"
        | value -> Ok value

    let private positiveInt name minimum maximum =
        match required name with
        | Error error -> Error error
        | Ok raw ->
            match Int32.TryParse raw with
            | true, value when value >= minimum && value <= maximum -> Ok value
            | _ -> Error $"{name} must be an integer from {minimum} through {maximum}"

    let private combine results =
        results
        |> List.choose (function Error value -> Some value | Ok _ -> None)
        |> function
            | [] -> Ok()
            | errors -> Error(String.concat "; " errors)

    let private value =
        function
        | Ok item -> item
        | Error error -> invalidOp error

    let internal loadWithSetsidLauncherAndTokenReader
        (tokenReader: string -> Result<string, string>)
        setsidLauncher
        =
        let runtime = required "FOGELL_DATABASE_URL"
        let maintenance = required "FOGELL_MAINTENANCE_DATABASE_URL"
        let tokenFile = required "FOGELL_API_TOKEN_FILE" |> Result.bind validateTokenFilePath
        let listen = required "FOGELL_LISTEN_URL"
        let stateRoot = required "FOGELL_STATE_ROOT" |> Result.bind validateStateRoot
        let runHost = required "FOGELL_RUN_HOST_PATH"
        let trustPool = required "FOGELL_LOCAL_TRUST_POOL"
        let setsid = validateSetsidLauncher setsidLauncher
        let maxPipeline = positiveInt "FOGELL_MAX_PIPELINE_BYTES" 1024 (16 * 1024 * 1024)
        let maxLogs = positiveInt "FOGELL_MAX_LOG_CHUNKS" 1 10000
        let poll = positiveInt "FOGELL_WORKER_POLL_MS" 25 60000
        let lease = positiveInt "FOGELL_WORKER_LEASE_SECONDS" 10 3600
        let workerTiming =
            match poll, lease with
            | Ok pollMilliseconds, Ok leaseSeconds ->
                validateWorkerTiming pollMilliseconds leaseSeconds
            | _ -> Ok()

        match combine [ runtime; maintenance; tokenFile; listen; stateRoot; runHost; trustPool; setsid ],
              combine
                  [ maxPipeline |> Result.map string
                    maxLogs |> Result.map string
                    poll |> Result.map string
                    lease |> Result.map string
                    workerTiming |> Result.map string ] with
        | Error error, _
        | _, Error error -> Error error
        | Ok _, Ok _ ->
            let runtimeValue = value runtime
            let maintenanceValue = value maintenance
            let tokenPath = value tokenFile
            let listenValue = value listen
            let stateRootValue = value stateRoot
            let runHostValue = value runHost |> Path.GetFullPath
            let setsidValue = value setsid

            let uriResult =
                match Uri.TryCreate(listenValue, UriKind.Absolute) with
                | true, uri when uri.Scheme = Uri.UriSchemeHttps -> Ok()
                | true, uri when uri.Scheme = Uri.UriSchemeHttp && uri.IsLoopback -> Ok()
                | _ -> Error "FOGELL_LISTEN_URL must be HTTPS or loopback HTTP"

            if runtimeValue = maintenanceValue then
                Error "runtime and maintenance database URLs must be distinct capabilities"
            elif not (isExecutableByServiceIdentity runHostValue) then
                Error "FOGELL_RUN_HOST_PATH does not name an executable file"
            else
                match uriResult with
                | Error error -> Error error
                | Ok _ ->
                    match tokenReader tokenPath with
                    | Error error -> Error error
                    | Ok rawToken ->
                        let token = rawToken.TrimEnd('\r', '\n')

                        if token.Length < 32 || token <> token.Trim() then
                            Error "API token file must contain at least 32 non-padded characters"
                        else
                            match prepareStateRoot stateRootValue with
                            | Error error -> Error error
                            | Ok _ ->
                                Ok
                                    { RuntimeDatabaseUrl = runtimeValue
                                      MaintenanceDatabaseUrl = maintenanceValue
                                      ApiToken = token
                                      ListenUrl = listenValue
                                      StateRoot = stateRootValue
                                      RunHostPath = runHostValue
                                      SetsidPath = setsidValue
                                      TrustPool = value trustPool
                                      MaxPipelineBytes = value maxPipeline
                                      MaxLogChunks = value maxLogs
                                      PollMilliseconds = value poll
                                      LeaseSeconds = value lease }

    let internal loadWithSetsidLauncher setsidLauncher =
        loadWithSetsidLauncherAndTokenReader readTokenFileSecurely setsidLauncher

    let load () = loadWithSetsidLauncher trustedSetsidLauncher
