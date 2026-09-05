namespace Fogell.Controller.Host

open System
open System.IO
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles
open Fogell.Domain

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

/// FG-026b (Codex #424 rounds 3 and 4). The descriptor layer shared by startup
/// validation and the file-drop connector, so startup exercises exactly the
/// opens the connector performs. The configured path string is resolved once,
/// to open the root with O_DIRECTORY|O_NOFOLLOW; everything else is relative to
/// that descriptor. Every read-side open is O_NONBLOCK and is followed by a
/// statx that requires a regular file with one link before any byte is read:
/// a FIFO, socket, device, directory or hard link planted by a same-UID
/// pipeline (FG-073) can therefore neither block the worker nor pass as a
/// marker or a receipt. Writes create only O_EXCL names of the connector's own
/// choosing and publish them with RENAME_NOREPLACE; directories are fsynced
/// after every entry they gain.
module internal DestinationDescriptor =

    [<Literal>]
    let marker = ".fogell-drop-root"

    /// The marker is an empty or tiny operator file; startup reads it through
    /// the descriptor and refuses anything larger, so a sparse giant cannot
    /// be substituted for it.
    [<Literal>]
    let markerMaxBytes = 4096L

    [<Literal>]
    let OpenReadOnly = 0

    [<Literal>]
    let OpenWriteOnly = 1

    [<Literal>]
    let OpenCreate = 0x40

    [<Literal>]
    let OpenExclusive = 0x80

    [<Literal>]
    let OpenNonBlocking = 0x800

    [<Literal>]
    let OpenCloseOnExec = 0x80000

    [<Literal>]
    let RenameNoReplace = 1u

    [<Literal>]
    let ENOENT = 2

    [<Literal>]
    let EEXIST = 17

    [<Literal>]
    let private AtEmptyPath = 0x1000

    [<Literal>]
    let private StatxType = 0x1u

    [<Literal>]
    let private StatxNlink = 0x4u

    [<Literal>]
    let private StatxSize = 0x200u

    [<Literal>]
    let private FileTypeMask = 0xF000us

    [<Literal>]
    let private RegularFileType = 0x8000us

    [<StructLayout(LayoutKind.Sequential, Size = 256)>]
    type LinuxStatx =
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
        end

    /// (device, inode): the physical identity of an open directory.
    type DirectoryIdentity = { DeviceMajor: uint32; DeviceMinor: uint32; Inode: uint64 }

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openPath(string path, int flags)

    [<DllImport("libc", EntryPoint = "openat", SetLastError = true)>]
    extern int private openAtNative(int directory, string name, int flags, int mode)

    [<DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)>]
    extern int private mkdirAtNative(int directory, string name, int mode)

    [<DllImport("libc", EntryPoint = "renameat2", SetLastError = true)>]
    extern int private renameAt2Native(int oldDirectory, string oldName, int newDirectory, string newName, uint32 flags)

    [<DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)>]
    extern int private unlinkAtNative(int directory, string name, int flags)

    [<DllImport("libc", EntryPoint = "fsync", SetLastError = true)>]
    extern int private fsyncNative(int descriptor)

    [<DllImport("libc", SetLastError = true)>]
    extern int private statx(int directoryFileDescriptor, string path, int flags, uint32 mask, LinuxStatx& buffer)

    let errno () = Marshal.GetLastPInvokeError()
    let owned (fd: int) = new SafeFileHandle(nativeint fd, true)
    let descriptor (handle: SafeFileHandle) = int (handle.DangerousGetHandle())

    let directoryFlags (table: LinuxOpenFlags.Table) =
        OpenReadOnly ||| table.Directory ||| table.NoFollow ||| OpenCloseOnExec

    /// Read-side opens never block: a FIFO with no writer opens at once and
    /// is then refused by the regular-file check instead of parking the worker.
    let fileReadFlags (table: LinuxOpenFlags.Table) =
        OpenReadOnly ||| table.NoFollow ||| OpenNonBlocking ||| OpenCloseOnExec

    let openAt (directory: SafeFileHandle) (name: string) (flags: int) (mode: int) =
        openAtNative (descriptor directory, name, flags, mode)

    let mkdirAt (directory: SafeFileHandle) (name: string) (mode: int) =
        mkdirAtNative (descriptor directory, name, mode)

    let renameAt2 (directory: SafeFileHandle) (oldName: string) (newName: string) (flags: uint32) =
        renameAt2Native (descriptor directory, oldName, descriptor directory, newName, flags)

    /// Only ever aimed at a name this process created with O_EXCL; never a
    /// directory (flags 0, never AT_REMOVEDIR).
    let unlinkAt (directory: SafeFileHandle) (name: string) =
        unlinkAtNative (descriptor directory, name, 0)

    type RegularFile = { Size: int64; Links: uint32 }

    /// statx through the open descriptor: the file must be a regular file.
    let regularFile (handle: SafeFileHandle) : Result<RegularFile, string> =
        let mutable status = Unchecked.defaultof<LinuxStatx>

        if statx (descriptor handle, "", AtEmptyPath, StatxType ||| StatxNlink ||| StatxSize, &status) <> 0 then
            Error $"cannot stat the open descriptor (errno {errno ()})"
        elif status.Mode &&& FileTypeMask <> RegularFileType then
            Error "not a regular file"
        else
            Ok { Size = int64 status.Size; Links = status.LinkCount }

    let sync (handle: SafeFileHandle) =
        if fsyncNative (descriptor handle) <> 0 then
            Error $"fsync failed (errno {errno ()})"
        else
            Ok()

    /// The one place the configured path string is resolved.
    let openRoot (table: LinuxOpenFlags.Table) (root: string) =
        let fd = openPath (root, directoryFlags table)

        if fd < 0 then
            Error $"cannot open {root} for reading as a directory without following links (errno {errno ()})"
        else
            Ok(owned fd)

    /// A directory opened for identity only, following a final symlink: the
    /// state root is whatever its configured path resolves to, and the
    /// disjointness check must compare against that real directory.
    let openDirectoryFollowingLinks (table: LinuxOpenFlags.Table) (path: string) =
        let fd = openPath (path, OpenReadOnly ||| table.Directory ||| OpenCloseOnExec)

        if fd < 0 then
            Error $"cannot open {path} for reading as a directory (errno {errno ()})"
        else
            Ok(owned fd)

    /// The marker seen through the root descriptor, never through the path:
    /// opened non-blocking, required to be a regular file with one link.
    let openMarker (table: LinuxOpenFlags.Table) (rootHandle: SafeFileHandle) : Result<SafeFileHandle, string> =
        let fd = openAt rootHandle marker (fileReadFlags table) 0

        if fd < 0 then
            let error = errno ()

            if error = ENOENT then
                Error $"{marker} marker is absent"
            else
                Error $"{marker} marker cannot be opened for reading (errno {error})"
        else
            let handle = owned fd

            match regularFile handle with
            | Ok file when file.Links = 1u -> Ok handle
            | Ok _ ->
                handle.Dispose()
                Error $"{marker} marker has more than one link"
            | Error error ->
                handle.Dispose()
                Error $"{marker} marker is {error}"

    let markerPinned (table: LinuxOpenFlags.Table) (rootHandle: SafeFileHandle) =
        match openMarker table rootHandle with
        | Error error -> Error error
        | Ok handle ->
            handle.Dispose()
            Ok()

    /// Exactly the regular file's bytes, at most `maxBytes`, through the open
    /// descriptor; anything else is a refusal, never a partial answer.
    let readBounded (handle: SafeFileHandle) (maxBytes: int64) : Result<byte array, string> =
        match regularFile handle with
        | Error error -> Error error
        | Ok file when file.Size > maxBytes -> Error $"is {file.Size} bytes, larger than the {maxBytes}-byte bound"
        | Ok file ->
            try
                use stream = new FileStream(handle, FileAccess.Read, 1, false)
                let buffer = Array.zeroCreate<byte> (int file.Size)
                stream.ReadExactly(buffer, 0, buffer.Length)

                if stream.ReadByte() <> -1 then
                    Error "grew while being read"
                else
                    Ok buffer
            with
            | :? EndOfStreamException -> Error "shrank while being read"
            | :? IOException as error -> Error $"could not be read: {error.Message}"

    [<Literal>]
    let private StatxInode = 0x100u

    /// The (device, inode) of an open directory through its descriptor.
    let identity (handle: SafeFileHandle) : Result<DirectoryIdentity, string> =
        let mutable status = Unchecked.defaultof<LinuxStatx>

        if statx (descriptor handle, "", AtEmptyPath, StatxType ||| StatxInode, &status) <> 0 then
            Error $"cannot stat the open directory (errno {errno ()})"
        else
            Ok { DeviceMajor = status.DeviceMajor; DeviceMinor = status.DeviceMinor; Inode = status.Inode }

    /// Codex #424 round 5: disjointness is decided physically, not lexically.
    /// Walk upward from the opened directory with openat(fd, "..") comparing
    /// (device, inode) against `ancestor` until the filesystem root (whose
    /// parent is itself). A symlinked ancestor in the configured path string
    /// cannot hide the relation, because every step is a real directory entry.
    let isWithin (start: SafeFileHandle) (ancestor: DirectoryIdentity) : Result<bool, string> =
        let table = LinuxOpenFlags.current

        match table with
        | Error error -> Error error
        | Ok table ->
            let rec walk (current: SafeFileHandle) (depth: int) =
                match identity current with
                | Error error -> Error error
                | Ok here when here = ancestor -> Ok true
                | Ok here ->
                    if depth > 4096 then
                        Error "directory walk exceeded 4096 levels"
                    else
                        let parentDescriptor = openAt current ".." (directoryFlags table) 0

                        if parentDescriptor < 0 then
                            Error $"cannot open a parent directory while walking upward (errno {errno ()})"
                        else
                            use parent = owned parentDescriptor

                            match identity parent with
                            | Error error -> Error error
                            | Ok above when above = here -> Ok false
                            | Ok _ -> walk parent (depth + 1)

            walk start 0

    let withPinnedRoot (root: string) (body: LinuxOpenFlags.Table -> SafeFileHandle -> Result<'a, string>) =
        match LinuxOpenFlags.current with
        | Error error -> Error $"destination root is not pinned: {error}"
        | Ok table ->
            match openRoot table root with
            | Error error -> Error $"destination root is not pinned: {error}"
            | Ok rootHandle ->
                use rootHandle = rootHandle

                match markerPinned table rootHandle with
                | Error error -> Error $"destination root is not pinned: {error}"
                | Ok() -> body table rootHandle

module EffectProducerConfig =
    let disabled = { FileDropRoot = None; KillAt = None }

    /// The operator creates this empty file inside the drop root. It pins the
    /// destination: startup refuses a root without it, and the connector
    /// refuses to write when it is gone (an unmounted or replaced volume has
    /// no marker), instead of recreating a root on whatever is underneath.
    [<Literal>]
    let fileDropRootMarker = DestinationDescriptor.marker

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

    /// Codex #424 round 4: startup exercises exactly the descriptor opens
    /// dispatch performs, so a root or marker that is writable but not
    /// readable (0300, 0200), a linked or non-regular marker, or an oversized
    /// one refuses startup by name instead of failing every completed build.
    /// Codex #424 round 5: the lexical StartsWith on GetFullPath cannot see a
    /// symlinked ancestor (`/srv/alias -> /srv/state`, root `/srv/alias/drop`).
    /// With both directories open, walk upward from each comparing
    /// (device, inode): the state root may be neither an ancestor of nor equal
    /// to the drop root, and the drop root may not contain the state root.
    let private validatePhysicallyDisjoint
        (table: LinuxOpenFlags.Table)
        (stateRoot: string)
        (rootHandle: SafeFileHandle)
        =
        match DestinationDescriptor.openDirectoryFollowingLinks table (Path.GetFullPath stateRoot) with
        | Error error -> Error $"FOGELL_STATE_ROOT cannot be opened to check that the drop root is disjoint from it: {error}"
        | Ok stateHandle ->
            use stateHandle = stateHandle

            match DestinationDescriptor.identity stateHandle, DestinationDescriptor.identity rootHandle with
            | Error error, _
            | _, Error error -> Error $"FOGELL_EFFECT_FILE_DROP_ROOT disjointness could not be decided: {error}"
            | Ok stateIdentity, Ok rootIdentity ->
                match DestinationDescriptor.isWithin rootHandle stateIdentity with
                | Error error -> Error $"FOGELL_EFFECT_FILE_DROP_ROOT disjointness could not be decided: {error}"
                | Ok true ->
                    Error "FOGELL_EFFECT_FILE_DROP_ROOT is physically inside (or is) FOGELL_STATE_ROOT, whatever the configured path spells"
                | Ok false ->
                    match DestinationDescriptor.isWithin stateHandle rootIdentity with
                    | Error error -> Error $"FOGELL_EFFECT_FILE_DROP_ROOT disjointness could not be decided: {error}"
                    | Ok true ->
                        Error "FOGELL_EFFECT_FILE_DROP_ROOT physically contains FOGELL_STATE_ROOT, whatever the configured path spells"
                    | Ok false -> Ok()

    let private validatePinnedForReading (stateRoot: string) (root: string) =
        match LinuxOpenFlags.current with
        | Error error -> Error $"FOGELL_EFFECT_FILE_DROP_ROOT cannot be pinned on this architecture: {error}"
        | Ok table ->
            match DestinationDescriptor.openRoot table root with
            | Error error -> Error $"FOGELL_EFFECT_FILE_DROP_ROOT is not readable as the pinned directory: {error}"
            | Ok rootHandle ->
                use rootHandle = rootHandle

                match validatePhysicallyDisjoint table stateRoot rootHandle with
                | Error error -> Error error
                | Ok() ->

                match DestinationDescriptor.openMarker table rootHandle with
                | Error error ->
                    Error $"FOGELL_EFFECT_FILE_DROP_ROOT must contain a readable regular {fileDropRootMarker} marker file: {error}"
                | Ok markerHandle ->
                    use markerHandle = markerHandle

                    match DestinationDescriptor.readBounded markerHandle DestinationDescriptor.markerMaxBytes with
                    | Error error -> Error $"FOGELL_EFFECT_FILE_DROP_ROOT marker {fileDropRootMarker} {error}"
                    | Ok _ -> Ok()

    /// The simulator destination must be an absolute, existing, writable
    /// directory that is disjoint from the controller state root and pinned by
    /// a readable regular marker file: a receipt is an external effect only if
    /// it lives outside the state the controller restores, and dispatch must be
    /// able to open at runtime exactly what startup accepted.
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
                elif not (File.Exists(Path.Combine(root, fileDropRootMarker))) then
                    Error $"FOGELL_EFFECT_FILE_DROP_ROOT must contain the operator-created {fileDropRootMarker} marker file"
                else
                    match validatePinnedForReading stateRoot root with
                    | Error error -> Error error
                    | Ok() ->
                        if not (probeWritable root) then
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
