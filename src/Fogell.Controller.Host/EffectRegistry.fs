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
    /// statx through the open descriptor: the file must be a regular file, and
    /// the type, link count and size must all have been reported (a filesystem
    /// that omits STATX_NLINK would otherwise read as zero links and be refused
    /// for the wrong reason — verifier P3-4 on #424).
    let regularFile (handle: SafeFileHandle) : Result<RegularFile, string> =
        let mutable status = Unchecked.defaultof<LinuxStatx>
        let required = StatxType ||| StatxNlink ||| StatxSize

        if statx (descriptor handle, "", AtEmptyPath, required, &status) <> 0 then
            Error $"cannot stat the open descriptor (errno {errno ()})"
        elif status.Mask &&& required <> required then
            Error "statx did not report type, link count and size for the open descriptor"
        elif status.Mode &&& FileTypeMask <> RegularFileType then
            Error "not a regular file"
        else
            Ok { Size = int64 status.Size; Links = status.LinkCount }

    [<Literal>]
    let private EINTR = 4

    /// fsync, retried on EINTR the way .NET's Flush(true) loops (verifier
    /// P3-4 on #424).
    let sync (handle: SafeFileHandle) =
        let rec attempt remaining =
            if fsyncNative (descriptor handle) = 0 then
                Ok()
            else
                let error = errno ()

                if error = EINTR && remaining > 0 then
                    attempt (remaining - 1)
                else
                    Error $"fsync failed (errno {error})"

        attempt 64

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
        let required = StatxType ||| StatxInode

        if statx (descriptor handle, "", AtEmptyPath, required, &status) <> 0 then
            Error $"cannot stat the open directory (errno {errno ()})"
        elif status.Mask &&& required <> required then
            Error "statx did not report type and inode for the open directory"
        else
            Ok { DeviceMajor = status.DeviceMajor; DeviceMinor = status.DeviceMinor; Inode = status.Inode }

    /// O_PATH: a descriptor that names a directory without opening it for
    /// reading, so the upward walk needs only search permission on each
    /// ancestor (a hardened 0711/0311 parent is common — verifier P2-3 on
    /// #424) and statx(AT_EMPTY_PATH) still answers on it. The bit is the same
    /// on every architecture .NET runs on.
    [<Literal>]
    let private OpenPath = 0x200000

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
                        let parentDescriptor =
                            openAt current ".." (OpenPath ||| table.Directory ||| table.NoFollow ||| OpenCloseOnExec) 0

                        if parentDescriptor < 0 then
                            Error $"cannot open a parent directory while walking upward (errno {errno ()})"
                        else
                            use parent = owned parentDescriptor

                            match identity parent with
                            | Error error -> Error error
                            | Ok above when above = here -> Ok false
                            | Ok _ -> walk parent (depth + 1)

            walk start 0

    [<Literal>]
    let private AtRemoveDirectory = 0x200

    /// Removes only the startup probe directory this process created; never
    /// used on an organization directory or anything an operator or pipeline
    /// wrote.
    let private rmdirAt (directory: SafeFileHandle) (name: string) =
        unlinkAtNative (descriptor directory, name, AtRemoveDirectory)

    /// The two syscalls whose failure the tests inject (Codex #424 round 9):
    /// a filesystem without RENAME_NOREPLACE at startup, and an unlink that
    /// fails on a read-only remount. `Error` carries errno.
    type WriteSyscalls =
        { RenameNoReplace: SafeFileHandle -> string -> string -> Result<unit, int>
          Unlink: SafeFileHandle -> string -> Result<unit, int> }

    let nativeWriteSyscalls =
        { RenameNoReplace =
            fun directory oldName newName ->
                if renameAt2 directory oldName newName RenameNoReplace >= 0 then Ok() else Error(errno ())
          Unlink = fun directory name -> if unlinkAt directory name >= 0 then Ok() else Error(errno ()) }

    [<Literal>]
    let ReceiptFileMode = 0o600

    [<Literal>]
    let OrganizationDirectoryMode = 0o755

    let writeBytes (stream: Stream) (bytes: byte array) = stream.Write(bytes, 0, bytes.Length)

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
        let made = mkdirAt rootHandle name OrganizationDirectoryMode

        if made < 0 && errno () <> EEXIST then
            Error $"mkdirat of the organization directory failed in the pinned destination (errno {errno ()})"
        else
            match sync rootHandle with
            | Error error -> Error $"fsync of the pinned root after mkdirat failed: {error}"
            | Ok() ->
                trace "root-fsynced"
                let fd = openAt rootHandle name (directoryFlags table) 0

                if fd < 0 then
                    Error $"openat of the organization directory failed in the pinned destination (errno {errno ()})"
                else
                    Ok(owned fd)

    /// The writer's exact sequence, shared by dispatch and the startup probe
    /// (Codex #424 round 9): mkdirat + fsync(root) + openat the organization
    /// directory; if the receipt name is free, openat(O_CREAT|O_EXCL) a temp
    /// of this process's choosing, write, Flush(true), renameat2(NOREPLACE),
    /// fsync(directory). The temp file's lifetime is one try/finally (round 8):
    /// every exit that did not rename it unlinks the process's own name through
    /// the directory descriptor, an exception becomes the error, and an unlink
    /// that itself fails is reported (`temp-unlink-failed:<errno>`) rather than
    /// traced as success. Every message names the syscall and errno.
    let writeReceipt
        (syscalls: WriteSyscalls)
        (trace: string -> unit)
        (writer: Stream -> byte array -> unit)
        (table: LinuxOpenFlags.Table)
        (rootHandle: SafeFileHandle)
        (organization: string)
        (fileName: string)
        (bytes: byte array)
        : Result<unit, string> =
        match openOrganizationDirectory trace table rootHandle organization with
        | Error error -> Error error
        | Ok directoryHandle ->
            use directoryHandle = directoryHandle
            let existing = openAt directoryHandle fileName (fileReadFlags table) 0

            if existing >= 0 then
                // The destination is idempotent per attempt: a receipt that
                // already exists is this connector's contract, and Confirm
                // decides whether its bytes are the ones this attempt meant to
                // leave. Whatever it is (a FIFO, a directory, a link) it is only
                // ever judged, never read here and never replaced.
                (owned existing).Dispose()
                trace "existing"
                Ok()
            else
                let temporary = fileName + $".{Guid.NewGuid():N}.tmp"

                let fd =
                    openAt
                        directoryHandle
                        temporary
                        (OpenWriteOnly ||| OpenCreate ||| OpenExclusive ||| table.NoFollow ||| OpenCloseOnExec)
                        ReceiptFileMode

                if fd < 0 then
                    // Nothing was created; nothing to clean.
                    Error $"openat(O_CREAT|O_EXCL) of the receipt temp file failed in the pinned destination (errno {errno ()})"
                else
                    let mutable renamed = false
                    let mutable unlinkFailure: int option = None

                    let published =
                        try
                            try
                                do
                                    use stream = new FileStream(owned fd, FileAccess.Write, 1, false)
                                    writer stream bytes
                                    stream.Flush true

                                trace "temp-fsynced"

                                match syscalls.RenameNoReplace directoryHandle temporary fileName with
                                | Ok() ->
                                    renamed <- true
                                    trace "renamed"
                                    Ok()
                                | Error error when error = EEXIST ->
                                    // Something now holds the receipt name: a
                                    // concurrent writer of the same attempt, or a
                                    // planted entry. Confirm judges it.
                                    Ok()
                                | Error error ->
                                    Error $"renameat2(RENAME_NOREPLACE) of the receipt failed in the pinned destination (errno {error})"
                            with ex ->
                                // ENOSPC, EIO, a closed descriptor: the effect may
                                // or may not have reached the destination; the row
                                // stays where it is for the trigger.
                                Error $"receipt write failed in the pinned destination: {ex.Message}"
                        finally
                            if not renamed then
                                // Only this process's own O_EXCL temp name is ever
                                // unlinked, through the directory descriptor, and
                                // never a directory.
                                match syscalls.Unlink directoryHandle temporary with
                                | Ok() -> trace "temp-unlinked"
                                | Error error ->
                                    unlinkFailure <- Some error
                                    trace $"temp-unlink-failed:{error}"

                    let published =
                        match unlinkFailure, published with
                        | Some error, Ok() ->
                            Error $"unlinkat of the receipt temp file {temporary} failed in the pinned destination (errno {error}); the orphan remains for the operator"
                        | Some error, Error reason ->
                            Error $"{reason}; unlinkat of the receipt temp file {temporary} also failed (errno {error}); the orphan remains for the operator"
                        | None, result -> result

                    match published with
                    | Error error -> Error error
                    | Ok() ->
                        // The directory entry is durable before the ledger may
                        // record applied or confirmed.
                        match sync directoryHandle with
                        | Error error -> Error $"fsync of the organization directory after renameat2 failed: {error}"
                        | Ok() ->
                            trace "organization-fsynced"
                            Ok()
    [<Literal>]
    let probeDirectoryPrefix = ".fogell-probe-"

    /// The startup probe (Codex #424 round 9, verifier P2-1): the writer's
    /// exact sequence, through the same code, against a per-process
    /// `.fogell-probe-<guid>` directory created here with mkdirat (EEXIST is a
    /// real refusal, never someone else's directory) under the pinned root,
    /// with a one-byte payload; then the probe receipt is unlinked and the
    /// probe directory removed, on the failure path as well. A leftover from a
    /// prober that died mid-way, or a second controller probing the same root,
    /// therefore cannot make this startup refuse. Whichever step fails names
    /// its syscall and errno; a filesystem without directory fsync or
    /// RENAME_NOREPLACE is refused at startup rather than on every build.
    let probeWrite (syscalls: WriteSyscalls) (table: LinuxOpenFlags.Table) (rootHandle: SafeFileHandle) =
        let probeDirectory = probeDirectoryPrefix + Guid.NewGuid().ToString "N"
        let fileName = $"probe-{Guid.NewGuid():N}.receipt"

        if mkdirAt rootHandle probeDirectory OrganizationDirectoryMode < 0 then
            Error $"mkdirat of the probe directory {probeDirectory} failed (errno {errno ()})"
        else
            let written =
                writeReceipt syscalls ignore writeBytes table rootHandle probeDirectory fileName [| 1uy |]

            let cleanup () =
                let directory = openAt rootHandle probeDirectory (directoryFlags table) 0

                let unlinked =
                    if directory < 0 then
                        Error(errno ())
                    else
                        use directoryHandle = owned directory
                        syscalls.Unlink directoryHandle fileName

                let removed = rmdirAt rootHandle probeDirectory
                let removeError = if removed < 0 then errno () else 0

                match unlinked with
                | Error error when error <> ENOENT -> Error $"unlinkat of the probe receipt failed (errno {error})"
                | _ when removed < 0 && removeError <> ENOENT -> Error $"rmdir of the probe directory {probeDirectory} failed (errno {removeError})"
                | _ -> Ok()

            match written with
            | Error error ->
                cleanup () |> ignore
                Error error
            | Ok() -> cleanup ()

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

    /// Codex #424 round 9: the startup probe is the writer's exact sequence
    /// through the descriptor layer (mkdirat, fsync, openat(O_CREAT|O_EXCL),
    /// write, Flush(true), renameat2(RENAME_NOREPLACE), fsync, unlinkat,
    /// rmdir of the probe directory), not a File.Open that proves only a
    /// create. A filesystem without directory fsync or RENAME_NOREPLACE is
    /// refused here, naming the syscall and errno, instead of failing every
    /// completed build later.
    let private probeWriteSequence (syscalls: DestinationDescriptor.WriteSyscalls) (root: string) =
        match LinuxOpenFlags.current with
        | Error error -> Error $"FOGELL_EFFECT_FILE_DROP_ROOT cannot be probed on this architecture: {error}"
        | Ok table ->
            match DestinationDescriptor.openRoot table root with
            | Error error -> Error $"FOGELL_EFFECT_FILE_DROP_ROOT is not readable as the pinned directory: {error}"
            | Ok rootHandle ->
                use rootHandle = rootHandle

                match DestinationDescriptor.probeWrite syscalls table rootHandle with
                | Error error -> Error $"FOGELL_EFFECT_FILE_DROP_ROOT does not support the receipt write sequence: {error}"
                | Ok() -> Ok()
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
    let internal validateFileDropRootWith (syscalls: DestinationDescriptor.WriteSyscalls) (stateRoot: string) (raw: string) =
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
                        match probeWriteSequence syscalls root with
                        | Error error -> Error error
                        | Ok() -> Ok root

    let validateFileDropRoot (stateRoot: string) (raw: string) =
        validateFileDropRootWith DestinationDescriptor.nativeWriteSyscalls stateRoot raw

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
