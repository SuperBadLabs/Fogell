namespace Fogell.Controller.Host

open System
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Text
open Microsoft.Win32.SafeHandles
open Fogell.Domain

/// Operator policy for the filesystem that contains controller workspaces,
/// stashes, and artifact staging.  These are pool limits: they do not claim to
/// reserve a portion of the pool for an individual attempt.
type StoragePoolPolicy =
    { PoolId: string
      MaxBytes: uint64
      MaxInodes: uint64
      MinFreeBytes: uint64
      MinFreeInodes: uint64 }

/// Linux identifies a mounted filesystem by device and mount instance.  A
/// later probe compares this complete value with the startup observation so an
/// unmount/replacement cannot silently turn a configured pool path into a
/// directory on the state-root filesystem.
type StoragePoolIdentity =
    { DeviceMajor: uint32
      DeviceMinor: uint32
      MountId: uint64 }

/// Raw capacity values from one open filesystem descriptor.  Bytes are blocks
/// available to the service identity, so filesystem-reserved blocks are not
/// counted as writable capacity.
type StoragePoolMetrics =
    { TotalBytes: uint64
      AvailableBytes: uint64
      TotalInodes: uint64
      AvailableInodes: uint64 }

/// A successful, descriptor-bound observation of the configured pool.
type StoragePoolObservation =
    { PoolId: string
      Identity: StoragePoolIdentity
      TotalBytes: uint64
      AvailableBytes: uint64
      TotalInodes: uint64
      AvailableInodes: uint64 }

/// Linux-specific bounded workspace-pool policy.  The operating-system
/// filesystem, not these arithmetic checks, is the write enforcement boundary.
/// `statvfs` is used only for a fail-closed admission/readiness observation.
module StoragePool =

    [<Literal>]
    let private poolDirectoryName = "workspaces"

    [<Literal>]
    let private markerName = ".fogell-pool-id"

    [<Literal>]
    let private maxMarkerBytes = 128UL

    [<Literal>]
    let private OpenReadOnly = 0

    [<Literal>]
    let private OpenNonBlocking = 0x800

    [<Literal>]
    let private OpenCloseOnExec = 0x80000

    [<Literal>]
    let private AtEmptyPath = 0x1000

    [<Literal>]
    let private StatxType = 0x1u

    [<Literal>]
    let private StatxMode = 0x2u

    [<Literal>]
    let private StatxInode = 0x100u

    [<Literal>]
    let private StatxSize = 0x200u

    [<Literal>]
    let private StatxMountId = 0x1000u

    [<Literal>]
    let private RegularFileType = 0x8000us

    [<Literal>]
    let private FileTypeMask = 0xF000us

    [<Literal>]
    let private PermissionMask = 0x0FFFus

    [<Literal>]
    let private MarkerMode = 0x0180us

    /// `struct statx`, including `stx_mnt_id` at offset 0x90.  This declaration
    /// intentionally targets Linux's 64-bit ABI; a 32-bit process refuses
    /// before calling either native capacity API below.
    [<StructLayout(LayoutKind.Sequential, Size = 256)>]
    type private LinuxStatx =
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
            val mutable Blocks: uint64
            val mutable AttributesMask: uint64
            val mutable AccessTimeSeconds: int64
            val mutable AccessTimeNanosecondsAndPad: uint64
            val mutable BirthTimeSeconds: int64
            val mutable BirthTimeNanosecondsAndPad: uint64
            val mutable ChangeTimeSeconds: int64
            val mutable ChangeTimeNanosecondsAndPad: uint64
            val mutable ModifyTimeSeconds: int64
            val mutable ModifyTimeNanosecondsAndPad: uint64
            val mutable RawDeviceMajor: uint32
            val mutable RawDeviceMinor: uint32
            val mutable DeviceMajor: uint32
            val mutable DeviceMinor: uint32
            val mutable MountId: uint64
        end

    /// Linux/glibc's 64-bit `struct statvfs`.  `unsigned long` and the block and
    /// inode counters are 64-bit on the explicitly supported ABI.
    [<StructLayout(LayoutKind.Sequential)>]
    type private LinuxStatVfs =
        struct
            val mutable BlockSize: uint64
            val mutable FragmentSize: uint64
            val mutable Blocks: uint64
            val mutable BlocksFree: uint64
            val mutable BlocksAvailable: uint64
            val mutable Files: uint64
            val mutable FilesFree: uint64
            val mutable FilesAvailable: uint64
            val mutable FileSystemId: uint64
            val mutable Flags: uint64
            val mutable NameMaximum: uint64
            val mutable Spare0: int32
            val mutable Spare1: int32
            val mutable Spare2: int32
            val mutable Spare3: int32
            val mutable Spare4: int32
            val mutable Spare5: int32
        end

    // Only f_type is consumed; reserve more than Linux's complete 64-bit
    // statfs structure so the native write is bounded on supported ABIs.
    [<StructLayout(LayoutKind.Sequential, Size = 256)>]
    type private LinuxStatFs =
        struct
            val mutable FileSystemType: int64
        end

    [<DllImport("libc", SetLastError = true)>]
    extern int private fstatfs(int descriptor, LinuxStatFs& buffer)

    let internal isSupportedFileSystem kind =
        // ext2/3/4 have a fixed inode table; tmpfs has a fixed nr_inodes limit.
        // Finite statvfs reports on dynamic-inode filesystems are insufficient.
        kind = 0xEF53L || kind = 0x01021994L

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openPath(string path, int flags)

    [<DllImport("libc", EntryPoint = "openat", SetLastError = true)>]
    extern int private openAtNative(int directoryDescriptor, string path, int flags, int mode)

    [<DllImport("libc", SetLastError = true)>]
    extern int private statx(int directoryFileDescriptor, string path, int flags, uint32 mask, LinuxStatx& buffer)

    [<DllImport("libc", SetLastError = true)>]
    extern int private fstatvfs(int descriptor, LinuxStatVfs& buffer)

    let private errno () = Marshal.GetLastPInvokeError()
    let private descriptor (handle: SafeFileHandle) = int (handle.DangerousGetHandle())
    let private owned fd = new SafeFileHandle(nativeint fd, true)

    let private directoryFlags (table: LinuxOpenFlags.Table) =
        OpenReadOnly ||| table.Directory ||| table.NoFollow ||| OpenCloseOnExec

    let private markerFlags (table: LinuxOpenFlags.Table) =
        OpenReadOnly ||| OpenNonBlocking ||| table.NoFollow ||| OpenCloseOnExec

    let private requiredVariableNames =
        [ "FOGELL_STORAGE_POOL_ID"
          "FOGELL_STORAGE_POOL_MAX_BYTES"
          "FOGELL_STORAGE_POOL_MAX_INODES"
          "FOGELL_STORAGE_POOL_MIN_FREE_BYTES"
          "FOGELL_STORAGE_POOL_MIN_FREE_INODES" ]

    let private isPoolId (value: string) =
        value.Length >= 1
        && value.Length <= 64
        && value
           |> Seq.forall (fun character ->
               (character >= 'a' && character <= 'z')
               || (character >= 'A' && character <= 'Z')
               || (character >= '0' && character <= '9')
               || character = '.'
               || character = '_'
               || character = '-')

    let private parsePositive name (raw: string) =
        if String.IsNullOrWhiteSpace raw || raw |> Seq.exists (fun character -> character < '0' || character > '9') then
            Error $"{name} must be a strict positive decimal integer"
        else
            match UInt64.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, value when value > 0UL -> Ok value
            | _ -> Error $"{name} must be a strict positive decimal integer"

    /// Parse the all-or-nothing operator policy.  An absent set disables pool
    /// policy; a partially supplied set is a configuration error rather than a
    /// fallback to an ordinary state-root subtree.
    let parse (environment: string -> string) : Result<StoragePoolPolicy option, string> =
        let values = requiredVariableNames |> List.map (fun name -> name, environment name)

        if values |> List.forall (fun (_, value) -> isNull value) then
            Ok None
        else
            let missing =
                values
                |> List.choose (fun (name, value) -> if String.IsNullOrWhiteSpace value then Some name else None)

            let requiredNames = String.concat ", " requiredVariableNames

            match missing with
            | _ :: _ -> Error $"storage-pool policy is incomplete; required variables: {requiredNames}"
            | [] ->
                let value name = values |> List.find (fun (candidate, _) -> candidate = name) |> snd
                let poolId = value "FOGELL_STORAGE_POOL_ID"

                if not (isPoolId poolId) then
                    Error "FOGELL_STORAGE_POOL_ID must be 1..64 ASCII alphanumeric, '.', '_' or '-' characters"
                else
                    match
                        parsePositive "FOGELL_STORAGE_POOL_MAX_BYTES" (value "FOGELL_STORAGE_POOL_MAX_BYTES"),
                        parsePositive "FOGELL_STORAGE_POOL_MAX_INODES" (value "FOGELL_STORAGE_POOL_MAX_INODES"),
                        parsePositive "FOGELL_STORAGE_POOL_MIN_FREE_BYTES" (value "FOGELL_STORAGE_POOL_MIN_FREE_BYTES"),
                        parsePositive "FOGELL_STORAGE_POOL_MIN_FREE_INODES" (value "FOGELL_STORAGE_POOL_MIN_FREE_INODES")
                    with
                    | Ok maxBytes, Ok maxInodes, Ok minFreeBytes, Ok minFreeInodes ->
                        if minFreeBytes > maxBytes then
                            Error "FOGELL_STORAGE_POOL_MIN_FREE_BYTES must not exceed FOGELL_STORAGE_POOL_MAX_BYTES"
                        elif minFreeInodes > maxInodes then
                            Error "FOGELL_STORAGE_POOL_MIN_FREE_INODES must not exceed FOGELL_STORAGE_POOL_MAX_INODES"
                        else
                            Ok(Some
                                { PoolId = poolId
                                  MaxBytes = maxBytes
                                  MaxInodes = maxInodes
                                  MinFreeBytes = minFreeBytes
                                  MinFreeInodes = minFreeInodes })
                    | Error error, _, _, _
                    | _, Error error, _, _
                    | _, _, Error error, _
                    | _, _, _, Error error -> Error error

    let private finiteTotal name value =
        if value = 0UL || value = UInt64.MaxValue then
            Error $"storage pool {name} is not a finite positive value"
        else
            Ok value

    /// Zero available blocks/inodes is a meaningful, finite pressure result.
    /// The all-ones sentinel is not: POSIX uses it for an unsupported count.
    let private finiteAvailability name value =
        if value = UInt64.MaxValue then
            Error $"storage pool {name} is not finite"
        else
            Ok value

    let private metricsFromStatVfs (status: LinuxStatVfs) =
        match finiteTotal "fragment size" status.FragmentSize, finiteTotal "block total" status.Blocks, finiteAvailability "block availability" status.BlocksAvailable, finiteTotal "inode total" status.Files, finiteAvailability "inode availability" status.FilesAvailable with
        | Ok fragmentSize, Ok blocks, Ok blocksAvailable, Ok files, Ok filesAvailable ->
            try
                let totalBytes = Checked.(*) fragmentSize blocks
                let availableBytes = Checked.(*) fragmentSize blocksAvailable

                if blocksAvailable > blocks || filesAvailable > files then
                    Error "storage pool availability exceeds its filesystem total"
                else
                    Ok
                        { TotalBytes = totalBytes
                          AvailableBytes = availableBytes
                          TotalInodes = files
                          AvailableInodes = filesAvailable }
            with :? OverflowException ->
                Error "storage pool block counters overflow 64-bit byte accounting"
        | Error error, _, _, _, _
        | _, Error error, _, _, _
        | _, _, Error error, _, _
        | _, _, _, Error error, _
        | _, _, _, _, Error error -> Error error

    /// Pure policy decision seam.  Tests can exercise every capacity,
    /// filesystem-identity, and marker branch without a mounted filesystem.
    let evaluate
        (policy: StoragePoolPolicy)
        (stateRootIdentity: StoragePoolIdentity)
        (poolIdentity: StoragePoolIdentity)
        (metrics: StoragePoolMetrics)
        (marker: string)
        : Result<StoragePoolObservation, string> =
        if poolIdentity.DeviceMajor = stateRootIdentity.DeviceMajor
           && poolIdentity.DeviceMinor = stateRootIdentity.DeviceMinor then
            Error "FOGELL storage pool workspaces must be a dedicated filesystem, not a state-root subtree"
        elif marker <> policy.PoolId then
            Error "FOGELL storage pool marker does not match FOGELL_STORAGE_POOL_ID"
        elif metrics.TotalBytes = 0UL || metrics.TotalBytes = UInt64.MaxValue then
            Error "FOGELL storage pool byte total is not finite"
        elif metrics.TotalInodes = 0UL || metrics.TotalInodes = UInt64.MaxValue then
            Error "FOGELL storage pool inode total is not finite"
        elif metrics.AvailableBytes > metrics.TotalBytes || metrics.AvailableInodes > metrics.TotalInodes then
            Error "FOGELL storage pool availability exceeds its total"
        elif metrics.TotalBytes > policy.MaxBytes then
            Error "FOGELL storage pool byte total exceeds FOGELL_STORAGE_POOL_MAX_BYTES"
        elif metrics.TotalInodes > policy.MaxInodes then
            Error "FOGELL storage pool inode total exceeds FOGELL_STORAGE_POOL_MAX_INODES"
        elif metrics.AvailableBytes < policy.MinFreeBytes then
            Error "FOGELL storage pool has insufficient available bytes"
        elif metrics.AvailableInodes < policy.MinFreeInodes then
            Error "FOGELL storage pool has insufficient available inodes"
        else
            Ok
                { PoolId = policy.PoolId
                  Identity = poolIdentity
                  TotalBytes = metrics.TotalBytes
                  AvailableBytes = metrics.AvailableBytes
                  TotalInodes = metrics.TotalInodes
                  AvailableInodes = metrics.AvailableInodes }

    /// Exact mounted-filesystem comparison for a later readiness or launch
    /// probe.  Device equality alone cannot distinguish a replacement mount.
    let sameIdentity (left: StoragePoolIdentity) (right: StoragePoolIdentity) =
        left = right

    let private identity (handle: SafeFileHandle) =
        let mutable status = Unchecked.defaultof<LinuxStatx>
        let required = StatxType ||| StatxInode ||| StatxMountId

        if statx (descriptor handle, "", AtEmptyPath, required, &status) <> 0 then
            Error $"storage pool statx failed (errno {errno ()})"
        elif status.Mask &&& required <> required then
            Error "storage pool statx did not report type, inode, and mount id"
        elif status.Mode &&& FileTypeMask <> 0x4000us then
            Error "storage pool descriptor is not a directory"
        else
            Ok
                { DeviceMajor = status.DeviceMajor
                  DeviceMinor = status.DeviceMinor
                  MountId = status.MountId }

    let internal identifyDirectory handle = identity handle

    let private openAbsoluteDirectoryWithoutLinks (table: LinuxOpenFlags.Table) (path: string) =
        if not (Path.IsPathFullyQualified path) then
            Error "FOGELL storage pool state root must be absolute"
        else
            let rootDescriptor = openPath (Path.GetPathRoot(path), directoryFlags table)

            if rootDescriptor < 0 then
                Error $"storage pool cannot open filesystem root without following links (errno {errno ()})"
            else
                let mutable current = owned rootDescriptor
                let mutable failure: string option = None

                try
                    let segments =
                        path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)

                    for segment in segments do
                        if Option.isNone failure then
                            if segment = "." || segment = ".." then
                                failure <- Some "FOGELL storage pool state root must not contain dot path components"
                            else
                                let child = openAtNative (descriptor current, segment, directoryFlags table, 0)

                                if child < 0 then
                                    failure <- Some $"storage pool cannot open state-root component '{segment}' without following links (errno {errno ()})"
                                else
                                    current.Dispose()
                                    current <- owned child

                    match failure with
                    | Some error ->
                        current.Dispose()
                        Error error
                    | None -> Ok current
                with ex ->
                    current.Dispose()
                    Error $"storage pool state-root descriptor walk failed ({ex.GetType().Name})"

    let private openWorkspacePool (table: LinuxOpenFlags.Table) (stateRoot: SafeFileHandle) =
        let fd = openAtNative (descriptor stateRoot, poolDirectoryName, directoryFlags table, 0)

        if fd < 0 then
            Error $"FOGELL storage pool workspaces directory must preexist as a non-symlink directory (errno {errno ()})"
        else
            Ok(owned fd)

    let private readMarker (table: LinuxOpenFlags.Table) (pool: SafeFileHandle) =
        let fd = openAtNative (descriptor pool, markerName, markerFlags table, 0)

        if fd < 0 then
            Error $"FOGELL storage pool marker is absent or cannot be opened without following links (errno {errno ()})"
        else
            use marker = owned fd
            let mutable status = Unchecked.defaultof<LinuxStatx>
            let required = StatxType ||| StatxMode ||| StatxSize

            if statx (descriptor marker, "", AtEmptyPath, required, &status) <> 0 then
                Error $"FOGELL storage pool marker statx failed (errno {errno ()})"
            elif status.Mask &&& required <> required then
                Error "FOGELL storage pool marker statx did not report type, mode, and size"
            elif status.Mode &&& FileTypeMask <> RegularFileType then
                Error "FOGELL storage pool marker must be a regular file"
            elif status.Mode &&& PermissionMask <> MarkerMode then
                Error "FOGELL storage pool marker must have exact mode 0600"
            elif status.Size > maxMarkerBytes then
                Error $"FOGELL storage pool marker exceeds {maxMarkerBytes} bytes"
            else
                try
                    use stream = new FileStream(marker, FileAccess.Read, 1, false)
                    let bytes = Array.zeroCreate<byte> (int status.Size)
                    stream.ReadExactly(bytes, 0, bytes.Length)

                    if stream.ReadByte() <> -1 then
                        Error "FOGELL storage pool marker changed while being read"
                    else
                        try
                            Ok(UTF8Encoding(false, true).GetString bytes)
                        with _ ->
                            Error "FOGELL storage pool marker is not valid UTF-8"
                with
                | :? EndOfStreamException -> Error "FOGELL storage pool marker shrank while being read"
                | :? IOException as error -> Error $"FOGELL storage pool marker cannot be read ({error.Message})"

    let private observeMetrics (pool: SafeFileHandle) =
        let mutable status = Unchecked.defaultof<LinuxStatVfs>
        let mutable filesystem = Unchecked.defaultof<LinuxStatFs>

        if fstatfs(descriptor pool, &filesystem) <> 0 then
            Error "storage pool filesystem type is unavailable"
        elif not (isSupportedFileSystem filesystem.FileSystemType) then
            Error "storage pool requires a fixed-inode ext filesystem or bounded tmpfs"
        elif fstatvfs (descriptor pool, &status) <> 0 then
            Error $"storage pool fstatvfs failed (errno {errno ()})"
        else
            metricsFromStatVfs status

    /// Observe the preexisting mounted `workspaces` filesystem through pinned
    /// descriptors.  No directory is created by this function; absence,
    /// symlinks, a missing mount id, or unsupported Linux ABI all refuse.
    let probe (stateRoot: string) (policy: StoragePoolPolicy) : Result<StoragePoolObservation, string> =
        if not (OperatingSystem.IsLinux()) then
            Error "FOGELL storage pool probing requires Linux"
        elif IntPtr.Size <> 8 then
            Error "FOGELL storage pool probing requires the Linux 64-bit statvfs ABI"
        else
            match LinuxOpenFlags.current with
            | Error error -> Error $"FOGELL storage pool descriptor policy is unavailable: {error}"
            | Ok table ->
                match openAbsoluteDirectoryWithoutLinks table stateRoot with
                | Error error -> Error error
                | Ok stateRootHandle ->
                    use stateRootHandle = stateRootHandle

                    match identity stateRootHandle, openWorkspacePool table stateRootHandle with
                    | Error error, opened ->
                        opened |> Result.iter (fun handle -> handle.Dispose())
                        Error error
                    | _, Error error -> Error error
                    | Ok stateRootIdentity, Ok poolHandle ->
                        use poolHandle = poolHandle

                        match identity poolHandle, observeMetrics poolHandle, readMarker table poolHandle with
                        | Error error, _, _ -> Error error
                        | _, Error error, _ -> Error error
                        | _, _, Error error -> Error error
                        | Ok poolIdentity, Ok metrics, Ok marker ->
                            evaluate policy stateRootIdentity poolIdentity metrics marker

    /// Re-probe and reject a mount/device replacement before caller work starts.
    let probePinned
        (stateRoot: string)
        (policy: StoragePoolPolicy)
        (expectedIdentity: StoragePoolIdentity)
        : Result<StoragePoolObservation, string> =
        probe stateRoot policy
        |> Result.bind (fun observed ->
            if sameIdentity expectedIdentity observed.Identity then
                Ok observed
            else
                Error "FOGELL storage pool filesystem identity changed since startup")
