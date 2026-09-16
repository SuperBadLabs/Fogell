namespace Fogell.Controller.Host

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open Microsoft.Win32.SafeHandles
open Fogell.Domain

/// One exclusive controller lease for the bounded execution filesystem.
///
/// The operator creates `<stateRoot>/workspaces/.fogell-pool-state` before a
/// controller starts.  It is deliberately a fixed-size, all-zero file rather
/// than a delete-on-completion lock: a controller that dies after admitting a
/// child leaves a dirty marker which a later controller must not erase or
/// reinterpret as proof that old writers are extinct.
module StoragePoolLease =

    [<Literal>]
    let private StateFileName = ".fogell-pool-state"

    [<Literal>]
    let private StateBytes = 4096

    [<Literal>]
    let private OpenReadWrite = 2

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
    let private StatxInode = 0x100u

    [<Literal>]
    let private StatxSize = 0x200u

    [<Literal>]
    let private RegularFileType = 0x8000us

    [<Literal>]
    let private FileTypeMask = 0xF000us

    [<Literal>]
    let private PermissionMask = 0x0FFFus

    [<Literal>]
    let private RequiredMode = 0x0180us // 0600

    [<Literal>]
    let private LockExclusiveNonBlocking = 6 // LOCK_EX | LOCK_NB

    [<Literal>]
    let private LockUnlock = 8 // LOCK_UN

    [<Literal>]
    let private WouldBlock = 11

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
            val mutable AccessSeconds: int64
            val mutable AccessNanoseconds: uint32
            val mutable Spare1: int32
            val mutable BirthSeconds: int64
            val mutable BirthNanoseconds: uint32
            val mutable Spare2: int32
            val mutable ChangeSeconds: int64
            val mutable ChangeNanoseconds: uint32
            val mutable Spare3: int32
            val mutable ModifySeconds: int64
            val mutable ModifyNanoseconds: uint32
            val mutable Spare4: int32
            val mutable RdevMajor: uint32
            val mutable RdevMinor: uint32
            val mutable DeviceMajor: uint32
            val mutable DeviceMinor: uint32
        end

    type internal FileIdentity =
        { DeviceMajor: uint32
          DeviceMinor: uint32
          Inode: uint64 }

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openFile(string path, int flags)

    [<DllImport("libc", EntryPoint = "openat", SetLastError = true)>]
    extern int private openAt(int directoryFileDescriptor, string path, int flags)

    [<DllImport("libc", EntryPoint = "flock", SetLastError = true)>]
    extern int private flock(int descriptor, int operation)

    [<DllImport("libc", SetLastError = true)>]
    extern int private statx(int directoryFileDescriptor, string path, int flags, uint32 mask, LinuxStatx& buffer)

    let private descriptor (handle: SafeFileHandle) = handle.DangerousGetHandle() |> int

    let private descriptorIdentity (handle: SafeFileHandle) : Result<FileIdentity, string> =
        let mutable status = Unchecked.defaultof<LinuxStatx>
        let required = StatxType ||| StatxMode ||| StatxInode ||| StatxSize

        if statx (descriptor handle, "", AtEmptyPath ||| AtNoAutomount, required, &status) <> 0 then
            Error "storage pool state metadata could not be read"
        elif status.Mask &&& required <> required then
            Error "storage pool state metadata is incomplete"
        elif status.Mode &&& FileTypeMask <> RegularFileType then
            Error "storage pool state must be a regular non-symlink file"
        elif status.Mode &&& PermissionMask <> RequiredMode then
            Error "storage pool state must have mode 0600"
        elif status.Size <> uint64 StateBytes then
            Error "storage pool state must be exactly 4096 bytes"
        else
            Ok
                { DeviceMajor = status.DeviceMajor
                  DeviceMinor = status.DeviceMinor
                  Inode = status.Inode }

    /// Open every directory component through an existing descriptor.  An
    /// operator must provision this tree first; creating a missing component
    /// here would turn a typo or a lost mount into a new, falsely trusted pool.
    let private openWorkspaceDirectory (table: LinuxOpenFlags.Table) (stateRoot: string) : Result<SafeFileHandle, string> =
        if not (Path.IsPathFullyQualified stateRoot) then
            Error "storage pool state root must be absolute"
        else
            let root = Path.GetPathRoot stateRoot
            let fullRoot = Path.GetFullPath stateRoot

            if String.IsNullOrEmpty root then
                Error "storage pool state root has no filesystem root"
            else
                let directoryFlags = OpenNonBlocking ||| OpenCloseOnExec ||| table.Directory ||| table.NoFollow
                let rootDescriptor = openFile(root, directoryFlags)

                if rootDescriptor < 0 then
                    Error "storage pool filesystem root could not be opened without following links"
                else
                    let mutable current = new SafeFileHandle(nativeint rootDescriptor, true)

                    try
                        let relative = Path.GetRelativePath(root, fullRoot)
                        let segments =
                            relative.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)

                        let mutable failure: string option = None

                        for segment in segments do
                            if failure.IsNone then
                                let next = openAt (descriptor current, segment, directoryFlags)

                                if next < 0 then
                                    failure <- Some "storage pool parent is missing, linked, or unavailable"
                                else
                                    let replacement = new SafeFileHandle(nativeint next, true)
                                    current.Dispose()
                                    current <- replacement

                        match failure with
                        | Some reason ->
                            current.Dispose()
                            Error reason
                        | None ->
                            let workspace = openAt (descriptor current, "workspaces", directoryFlags)
                            current.Dispose()

                            if workspace < 0 then
                                Error "storage pool workspace root is missing, linked, or unavailable"
                            else
                                Ok(new SafeFileHandle(nativeint workspace, true))
                    with _ ->
                        current.Dispose()
                        Error "storage pool parent validation failed"

    let private openStateFile (table: LinuxOpenFlags.Table) stateRoot : Result<SafeFileHandle * FileIdentity, string> =
        match openWorkspaceDirectory table stateRoot with
        | Error error -> Error error
        | Ok workspace ->
            use workspace = workspace
            let flags = OpenReadWrite ||| OpenNonBlocking ||| OpenCloseOnExec ||| table.NoFollow
            let raw = openAt (descriptor workspace, StateFileName, flags)

            if raw < 0 then
                Error "storage pool state must be an existing regular nofollow file"
            else
                let handle = new SafeFileHandle(nativeint raw, true)

                match descriptorIdentity handle with
                | Ok identity -> Ok(handle, identity)
                | Error error ->
                    handle.Dispose()
                    Error error

    let private sameIdentity expected actual =
        expected.DeviceMajor = actual.DeviceMajor
        && expected.DeviceMinor = actual.DeviceMinor
        && expected.Inode = actual.Inode

    type PoolLease internal (stateRoot: string, table: LinuxOpenFlags.Table, handle: SafeFileHandle, identity: FileIdentity, poolHandle: SafeFileHandle, poolIdentity: StoragePoolIdentity) =
        let stream = new FileStream(handle, FileAccess.ReadWrite, StateBytes, false)
        let gate = obj ()
        let mutable disposed = false

        let verifyCurrentPath () =
            if disposed then
                Error "storage pool lease is disposed"
            else
                match descriptorIdentity handle with
                | Error error -> Error error
                | Ok openIdentity when not (sameIdentity identity openIdentity) ->
                    Error "storage pool state descriptor identity changed"
                | Ok _ ->
                    match openStateFile table stateRoot with
                    | Error error -> Error error
                    | Ok(current, currentIdentity) ->
                        use current = current

                        if sameIdentity identity currentIdentity then
                            Ok()
                        else
                            Error "storage pool state path was replaced while leased"

        let readState () =
            let bytes = Array.zeroCreate<byte> StateBytes
            stream.Position <- 0L
            let mutable offset = 0

            while offset < bytes.Length do
                let read = stream.Read(bytes, offset, bytes.Length - offset)

                if read = 0 then
                    failwith "storage pool state became shorter than 4096 bytes"

                offset <- offset + read

            bytes

        let writeState (bytes: byte array) =
            if bytes.Length <> StateBytes then
                invalidArg (nameof bytes) "storage pool state writes must be exactly 4096 bytes"

            stream.Position <- 0L
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush true

        let isIdle (bytes: byte array) = bytes |> Array.forall ((=) 0uy)

        member _.Identity = poolIdentity

        member _.CheckIdle() : Result<unit, string> =
            lock gate (fun () ->
                try
                    verifyCurrentPath ()
                    |> Result.bind (fun () ->
                        if readState () |> isIdle then
                            Ok()
                        else
                            Error "storage pool is dirty; manual reconciliation must prove old writers are extinct")
                with _ ->
                    Error "storage pool state could not be checked")

        member _.MarkActive(attemptIdentity: string) : Result<unit, string> =
            lock gate (fun () ->
                if String.IsNullOrWhiteSpace attemptIdentity then
                    Error "storage pool active identity is required"
                else
                    try
                        verifyCurrentPath ()
                        |> Result.bind (fun () ->
                            if not (readState () |> isIdle) then
                                Error "storage pool is dirty; manual reconciliation must prove old writers are extinct"
                            else
                                let identityBytes = Encoding.UTF8.GetBytes attemptIdentity

                                if identityBytes.Length > StateBytes - 2 then
                                    Error "storage pool active identity is too long"
                                else
                                    let state = Array.zeroCreate<byte> StateBytes
                                    state[0] <- 1uy
                                    Array.Copy(identityBytes, 0, state, 1, identityBytes.Length)
                                    // The trailing guard makes an interrupted
                                    // zeroing pass fail closed: a short write from
                                    // offset zero cannot turn an active record into
                                    // an all-zero (idle) record.
                                    state[StateBytes - 1] <- 0xA5uy
                                    // A caller may launch only after this full buffer
                                    // reached stable storage.  Thus a crash before this
                                    // method returns cannot have admitted a child.
                                    writeState state
                                    verifyCurrentPath ())
                    with _ ->
                        Error "storage pool state could not be marked active")

        member _.Complete() : Result<unit, string> =
            lock gate (fun () ->
                try
                    verifyCurrentPath ()
                    |> Result.bind (fun () ->
                        if readState () |> isIdle then
                            Error "storage pool is already idle"
                        else
                            // Only the worker's successful terminal and proven
                            // no-child paths may call this.  A failed write never
                            // clears an existing dirty marker.
                            writeState (Array.zeroCreate<byte> StateBytes)
                            verifyCurrentPath ())
                with _ ->
                    Error "storage pool state could not be completed")

        interface IDisposable with
            member _.Dispose() =
                lock gate (fun () ->
                    if not disposed then
                        disposed <- true
                        try
                            flock(descriptor handle, LockUnlock) |> ignore
                        finally
                            try
                                stream.Dispose()
                            finally
                                handle.Dispose()
                                poolHandle.Dispose())

    let tryOpen (stateRoot: string) : Result<PoolLease, string> =
        if not (OperatingSystem.IsLinux()) then
            Error "storage pool lease requires Linux"
        else
            match LinuxOpenFlags.current with
            | Error reason -> Error $"storage pool descriptor policy is unavailable: {reason}"
            | Ok table ->
                match openWorkspaceDirectory table stateRoot with
                | Error error -> Error error
                | Ok poolHandle ->
                    // The mounted directory is the primary lock identity. A
                    // replacement state file must not create a second lock
                    // domain while a controller still owns this filesystem.
                    if flock(descriptor poolHandle, LockExclusiveNonBlocking) <> 0 then
                        poolHandle.Dispose()
                        Error "storage pool is already leased by another controller or its lock is unavailable"
                    else
                        match openStateFile table stateRoot with
                        | Error error ->
                            poolHandle.Dispose()
                            Error error
                        | Ok(handle, identity) ->
                            if flock(descriptor handle, LockExclusiveNonBlocking) <> 0 then
                                handle.Dispose()
                                poolHandle.Dispose()
                                Error "storage pool state is already leased or its lock is unavailable"
                            else
                                try
                                    match StoragePool.identifyDirectory poolHandle with
                                    | Ok poolIdentity -> Ok(new PoolLease(stateRoot, table, handle, identity, poolHandle, poolIdentity))
                                    | Error error ->
                                        handle.Dispose()
                                        poolHandle.Dispose()
                                        Error error
                                with _ ->
                                    handle.Dispose()
                                    poolHandle.Dispose()
                                    Error "storage pool lease could not be initialized"
