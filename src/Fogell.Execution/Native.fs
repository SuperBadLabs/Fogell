namespace Fogell.Execution

open System
open System.IO
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

/// Fogell.Execution's DllImport surface (ADR 0006). Every entry point is
/// documented. `realpath(path, NULL)` is the one native allocation: its pointer
/// is converted and freed in the same helper before any result escapes. Besides signal
/// and process-group primitives, the containment anchor uses Linux subreaper
/// ownership plus nonblocking waitpid so a zombie cannot keep a joinable group
/// alive after useful execution is extinct.
module internal Native =

    [<Literal>]
    let private OpenReadOnly = 0

    [<Literal>]
    let private OpenWriteOnly = 1

    [<Literal>]
    let private OpenCreate = 0x40

    [<Literal>]
    let private OpenTruncate = 0x200

    [<Literal>]
    let private OpenNonBlocking = 0x800

    [<Literal>]
    let private OpenDirectory = 0x10000

    [<Literal>]
    let private OpenNoFollow = 0x20000

    [<Literal>]
    let private OpenCloseOnExec = 0x80000

    [<RequireQualifiedAccess>]
    type ProcessGroupQuery =
        | Found of int
        | Absent
        | Uncertain

    [<RequireQualifiedAccess>]
    type ProcessGroupPresence =
        | Present
        | Absent
        | Uncertain

    [<RequireQualifiedAccess>]
    type ChildReapResult =
        | Reaped
        | Running
        | NotChild
        | Uncertain

    /// POSIX signals Fogell uses. Values are the Linux/glibc numbers.
    [<Literal>]
    let SIGTERM = 15

    [<Literal>]
    let SIGKILL = 9

    [<Literal>]
    let SIGCONT = 18

    [<Literal>]
    let ESRCH = 3 // no such process

    [<Literal>]
    let private ECHILD = 10

    [<Literal>]
    let private WNOHANG = 1

    [<Literal>]
    let private PR_SET_CHILD_SUBREAPER = 36

    /// `kill(2)`. A negative pid targets the whole process group, which is how
    /// a step's descendants are signalled together rather than orphaned.
    [<DllImport("libc", SetLastError = true)>]
    extern int private kill(int pid, int signum)

    /// `getpgid(2)`. Used only to confirm a child really did lead a new group,
    /// so the tests assert the mechanism rather than trusting it.
    [<DllImport("libc", SetLastError = true)>]
    extern int private getpgid(int pid)

    /// `prctl(PR_SET_CHILD_SUBREAPER)`. Registered anchors orphaned by their
    /// session leader are reparented to Run.Host instead of an arbitrary PID 1.
    [<DllImport("libc", SetLastError = true)>]
    extern int private prctl(int option, nativeint arg2, nativeint arg3, nativeint arg4, nativeint arg5)

    /// `waitpid(2)` with a null status pointer. Callers pass only a PID observed
    /// in the registered group currently being reconciled; they never use -1 or
    /// harvest an unrelated concurrently running step's child.
    [<DllImport("libc", SetLastError = true)>]
    extern int private waitpid(int pid, nativeint status, int options)

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openFile(string path, int flags)

    [<DllImport("libc", EntryPoint = "openat", SetLastError = true)>]
    extern int private openFileAt(int directoryDescriptor, string path, int flags, int mode)

    [<DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)>]
    extern int private makeDirectoryAt(int directoryDescriptor, string path, int mode)

    [<DllImport("libc", EntryPoint = "realpath", SetLastError = true)>]
    extern nativeint private realpath(string path, nativeint resolvedPath)

    [<DllImport("libc", EntryPoint = "free")>]
    extern void private free(nativeint pointer)

    let private physicalPath path =
        let pointer = realpath (path, nativeint 0)

        if pointer = nativeint 0 then
            None
        else
            try
                Marshal.PtrToStringUTF8 pointer |> Option.ofObj
            finally
                free pointer

    let private canonicalDirectoryPath path =
        path |> Path.GetFullPath |> Path.TrimEndingDirectorySeparator

    /// Distinguish an honestly absent directory from ENOENT caused by a dangling
    /// symlink ancestor. Walk from the filesystem root through live directory
    /// descriptors; the first genuinely missing component proves absence, while
    /// O_NOFOLLOW turns every symlink component into a refusal.
    let private proveDirectoryMissingWithoutLinks (absoluteRoot: string) =
        let systemRoot = Path.GetPathRoot absoluteRoot |> canonicalDirectoryPath
        let flags =
            OpenReadOnly
            ||| OpenNonBlocking
            ||| OpenDirectory
            ||| OpenNoFollow
            ||| OpenCloseOnExec
        let rootDescriptor = openFile(systemRoot, flags)

        if rootDescriptor < 0 then
            Error $"filesystem root cannot be opened without following a link (errno {Marshal.GetLastPInvokeError()})"
        else
            let mutable current = new SafeFileHandle(nativeint rootDescriptor, true)

            try
                let segments =
                    Path.GetRelativePath(systemRoot, absoluteRoot)
                        .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                let mutable missing = false
                let mutable failure: string option = None

                for segment in segments do
                    if not missing && Option.isNone failure then
                        let child = openFileAt(current.DangerousGetHandle() |> int, segment, flags, 0)

                        if child >= 0 then
                            current.Dispose()
                            current <- new SafeFileHandle(nativeint child, true)
                        else
                            let code = Marshal.GetLastPInvokeError()
                            if code = 2 then
                                missing <- true
                            else
                                failure <-
                                    Some $"scan root ancestor is linked or unavailable (errno {code})"

                match failure, missing with
                | Some why, _ -> Error why
                | None, true -> Ok()
                | None, false -> Error "scan root changed while absence was being proved"
            finally
                current.Dispose()

    /// Open the selected scan root itself without following a final link and
    /// require its descriptor to retain the exact physical root identity.
    let openDirectoryIfPresentWithoutLinks
        (root: string)
        : Result<SafeFileHandle option, string> =
        if not (OperatingSystem.IsLinux()) then
            Error "stash link containment requires Linux descriptor semantics"
        else
            try
                let lexicalRoot = canonicalDirectoryPath root
                let descriptor =
                    openFile (
                        lexicalRoot,
                        OpenReadOnly
                        ||| OpenNonBlocking
                        ||| OpenDirectory
                        ||| OpenNoFollow
                        ||| OpenCloseOnExec)

                if descriptor < 0 then
                    let code = Marshal.GetLastPInvokeError()
                    if code = 2 then
                        match proveDirectoryMissingWithoutLinks lexicalRoot with
                        | Ok() -> Ok None
                        | Error why -> Error why
                    else
                        Error $"scan root cannot be opened without following a link (errno {code})"
                else
                    let handle = new SafeFileHandle(nativeint descriptor, true)
                    match physicalPath $"/proc/self/fd/{descriptor}" with
                    | Some physicalRoot when String.Equals(physicalRoot, lexicalRoot, StringComparison.Ordinal) ->
                        Ok(Some handle)
                    | _ ->
                        handle.Dispose()
                        Error "scan root is a symbolic link or escaped object"
            with ex ->
                Error $"scan root validation failed ({ex.GetType().Name})"

    let openDirectoryWithoutLinks (root: string) : Result<SafeFileHandle, string> =
        match openDirectoryIfPresentWithoutLinks root with
        | Ok(Some handle) -> Ok handle
        | Ok None -> Error "scan root is missing"
        | Error why -> Error why

    /// Open one child directory relative to an already trusted live directory
    /// descriptor. O_NOFOLLOW closes the check/reopen race: replacing the child
    /// with a link before this call makes the open fail instead of traversing it.
    let openChildDirectoryWithoutLinks
        (parent: SafeFileHandle)
        (name: string)
        : Result<SafeFileHandle, string> =
        if
            String.IsNullOrEmpty name
            || name = "."
            || name = ".."
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar)
        then
            Error "scan child is not one strict path segment"
        else
            let descriptor =
                openFileAt(
                    parent.DangerousGetHandle() |> int,
                    name,
                    OpenReadOnly
                    ||| OpenNonBlocking
                    ||| OpenDirectory
                    ||| OpenNoFollow
                    ||| OpenCloseOnExec,
                    0)

            if descriptor < 0 then
                Error $"scan directory is linked, missing, or unavailable (errno {Marshal.GetLastPInvokeError()})"
            else
                Ok(new SafeFileHandle(nativeint descriptor, true))

    let directoryDescriptorPath (handle: SafeFileHandle) =
        $"/proc/self/fd/{handle.DangerousGetHandle() |> int}"

    let fileMode (stream: FileStream) =
        File.GetUnixFileMode(stream.SafeFileHandle)

    let fileLastWriteTimeUtc (stream: FileStream) =
        File.GetLastWriteTimeUtc(stream.SafeFileHandle)

    let setFileMode (stream: FileStream) (mode: UnixFileMode) =
        File.SetUnixFileMode(stream.SafeFileHandle, mode)

    let setFileLastWriteTimeUtc (stream: FileStream) (value: DateTime) =
        File.SetLastWriteTimeUtc(stream.SafeFileHandle, value)

    /// Open one selected workspace file through a descriptor and prove that the
    /// descriptor still names the exact lexical path beneath the exact physical
    /// workspace root. `O_NOFOLLOW` rejects a final file link; the descriptor's
    /// physical path also exposes every directory link in its ancestor chain.
    /// The returned stream owns the descriptor, so copying from it cannot be
    /// redirected by a pathname swap after this check.
    let openFileWithoutLinks (root: string) (relative: string) : Result<FileStream, string> =
        if not (OperatingSystem.IsLinux()) then
            Error "stash link containment requires Linux descriptor semantics"
        elif
            String.IsNullOrEmpty relative
            || Path.IsPathFullyQualified relative
            || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               |> Array.exists (fun segment -> segment = "" || segment = "." || segment = "..")
        then
            Error "selected path is not a strict relative path"
        else
            try
                let lexicalRoot = canonicalDirectoryPath root
                let lexicalCandidate = Path.GetFullPath(Path.Combine(lexicalRoot, relative))
                let prefix = lexicalRoot.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

                if not (lexicalCandidate.StartsWith(prefix, StringComparison.Ordinal)) then
                    Error "selected path escapes the workspace"
                else
                    match physicalPath lexicalRoot with
                    | None -> Error "workspace root cannot be resolved"
                    | Some physicalRoot when not (String.Equals(physicalRoot, lexicalRoot, StringComparison.Ordinal)) ->
                        Error "workspace root is a symbolic link"
                    | Some physicalRoot ->
                        let descriptor =
                            openFile (
                                lexicalCandidate,
                                OpenReadOnly ||| OpenNonBlocking ||| OpenNoFollow ||| OpenCloseOnExec)

                        if descriptor < 0 then
                            let code = Marshal.GetLastPInvokeError()
                            Error $"selected path cannot be opened without following a link (errno {code})"
                        else
                            let handle = new SafeFileHandle(nativeint descriptor, true)

                            try
                                let stream = new FileStream(handle, FileAccess.Read, 65536, false)

                                try
                                    let descriptorPath = $"/proc/self/fd/{descriptor}"

                                    match physicalPath descriptorPath with
                                    | Some openedPath
                                        when String.Equals(openedPath, lexicalCandidate, StringComparison.Ordinal)
                                             && not (Directory.Exists descriptorPath)
                                             && stream.CanSeek ->
                                        // Force seekable-file metadata acquisition before
                                        // handing the descriptor to the copy boundary. This
                                        // rejects directories and streams such as FIFOs; the
                                        // FG-228 claim deliberately does not cover hard links,
                                        // device nodes, or mount substitution.
                                        stream.Length |> ignore
                                        Ok stream
                                    | _ ->
                                        stream.Dispose()
                                        Error "selected path is a symbolic link, non-seekable file, or escaped object"
                                with ex ->
                                    // Once FileStream owns the SafeFileHandle it is the
                                    // resource boundary. Dispose the stream itself if
                                    // descriptor validation or metadata acquisition fails.
                                    stream.Dispose()
                                    Error $"selected path descriptor is unreadable ({ex.GetType().Name})"
                            with ex ->
                                // Construction failed before ownership transferred.
                                handle.Dispose()
                                Error $"selected path descriptor is unreadable ({ex.GetType().Name})"
            with ex ->
                Error $"selected path validation failed ({ex.GetType().Name})"

    /// Create or replace one workspace file relative to directory descriptors.
    /// Every ancestor is opened with O_DIRECTORY|O_NOFOLLOW and the final leaf
    /// with O_NOFOLLOW, so a symlink swap cannot redirect unstash outside the
    /// workspace between validation and write.
    let createWorkspaceFileWithoutLinks
        (workspace: string)
        (relative: string)
        : Result<FileStream, string> =
        if not (OperatingSystem.IsLinux()) then
            Error "unstash link containment requires Linux descriptor semantics"
        elif
            String.IsNullOrEmpty relative
            || Path.IsPathFullyQualified relative
            || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               |> Array.exists (fun segment -> segment = "" || segment = "." || segment = "..")
        then
            Error "restore path is not a strict relative path"
        else
            let root = canonicalDirectoryPath workspace
            let rootDescriptor =
                openFile (
                    root,
                    OpenReadOnly
                    ||| OpenNonBlocking
                    ||| OpenDirectory
                    ||| OpenNoFollow
                    ||| OpenCloseOnExec)

            if rootDescriptor < 0 then
                Error $"workspace root cannot be opened without following a link (errno {Marshal.GetLastPInvokeError()})"
            else
                let mutable current = new SafeFileHandle(nativeint rootDescriptor, true)

                try
                    let rootDescriptorPath = $"/proc/self/fd/{rootDescriptor}"

                    match physicalPath rootDescriptorPath with
                    | Some physicalRoot when String.Equals(physicalRoot, root, StringComparison.Ordinal) ->
                        let segments = relative.Split(Path.DirectorySeparatorChar)
                        let directories = segments |> Array.take (segments.Length - 1)
                        let mutable failure: string option = None

                        for segment in directories do
                            if Option.isNone failure then
                                let parent = current.DangerousGetHandle() |> int
                                let flags =
                                    OpenReadOnly
                                    ||| OpenNonBlocking
                                    ||| OpenDirectory
                                    ||| OpenNoFollow
                                    ||| OpenCloseOnExec
                                let mutable child = openFileAt(parent, segment, flags, 0)

                                if child < 0 && Marshal.GetLastPInvokeError() = 2 then
                                    // Mode 0777 subject to the process umask,
                                    // matching ordinary Directory.CreateDirectory
                                    // semantics while retaining descriptor-relative
                                    // no-follow creation.
                                    let made = makeDirectoryAt(parent, segment, 511)
                                    if made = 0 || Marshal.GetLastPInvokeError() = 17 then
                                        child <- openFileAt(parent, segment, flags, 0)

                                if child < 0 then
                                    failure <-
                                        Some $"restore directory component is missing, linked, or unavailable (errno {Marshal.GetLastPInvokeError()})"
                                else
                                    current.Dispose()
                                    current <- new SafeFileHandle(nativeint child, true)

                        match failure with
                        | Some why -> Error why
                        | None ->
                            let leaf = segments.[segments.Length - 1]
                            let descriptor =
                                openFileAt(
                                    current.DangerousGetHandle() |> int,
                                    leaf,
                                    OpenWriteOnly
                                    ||| OpenCreate
                                    ||| OpenTruncate
                                    ||| OpenNoFollow
                                    ||| OpenCloseOnExec,
                                    384)

                            if descriptor < 0 then
                                Error $"restore target is linked or unavailable (errno {Marshal.GetLastPInvokeError()})"
                            else
                                let handle = new SafeFileHandle(nativeint descriptor, true)
                                try
                                    Ok(new FileStream(handle, FileAccess.Write, 65536, false))
                                with ex ->
                                    handle.Dispose()
                                    Error $"restore target descriptor is unwritable ({ex.GetType().Name})"
                    | _ -> Error "workspace root is a symbolic link or escaped object"
                finally
                    current.Dispose()

    /// Signal a single process. Returns false when it no longer exists.
    let signalProcess (pid: int) (signum: int) : bool =
        if pid <= 0 then
            false
        else
            kill (pid, signum) = 0

    /// This process's own group. Signalling it would kill the controller, so
    /// [signalGroup] refuses to do so.
    let ownProcessGroup () : int =
        match getpgid 0 with
        | -1 -> -1
        | g -> g

    /// Signal an entire process group. `pgid` is positive; the negation is
    /// applied here so callers cannot get the sign wrong.
    ///
    /// SAFETY GUARD: refuses to signal our own process group. Learned the hard
    /// way — `setsid --wait` forks, so the pid .NET reports is setsid's, whose
    /// group is the *parent's*. Reaping therefore sent SIGTERM to the controller
    /// itself. The pgid derivation is fixed, and this guard makes the whole
    /// class of mistake unable to recur.
    let signalGroup (pgid: int) (signum: int) : bool =
        if pgid <= 0 then
            false
        elif pgid = ownProcessGroup () then
            false
        else
            kill (-pgid, signum) = 0

    let probeProcessGroup (pgid: int) =
        if pgid <= 1 then
            ProcessGroupPresence.Uncertain
        else
            let result = kill (-pgid, 0)

            if result = 0 then
                ProcessGroupPresence.Present
            else
                match Marshal.GetLastWin32Error() with
                | ESRCH -> ProcessGroupPresence.Absent
                | 1 -> ProcessGroupPresence.Present
                | _ -> ProcessGroupPresence.Uncertain

    let enableChildSubreaper () =
        prctl(PR_SET_CHILD_SUBREAPER, nativeint 1, nativeint 0, nativeint 0, nativeint 0) = 0

    let tryReapChild pid =
        if pid <= 1 then
            ChildReapResult.Uncertain
        else
            let result = waitpid(pid, nativeint 0, WNOHANG)

            if result = pid then
                ChildReapResult.Reaped
            elif result = 0 then
                ChildReapResult.Running
            elif Marshal.GetLastWin32Error() = ECHILD then
                ChildReapResult.NotChild
            else
                ChildReapResult.Uncertain

    /// True when the process (or group leader) is still present.
    let processExists (pid: int) : bool = pid > 0 && kill (pid, 0) = 0

    let internal classifyProcessGroupQuery result error =
        if result >= 0 then
            ProcessGroupQuery.Found result
        elif error = ESRCH then
            ProcessGroupQuery.Absent
        else
            ProcessGroupQuery.Uncertain

    let queryProcessGroup (pid: int) =
        if pid <= 0 then
            ProcessGroupQuery.Absent
        else
            let result = getpgid pid
            classifyProcessGroupQuery result (Marshal.GetLastWin32Error())

    let processGroupOf (pid: int) : int option =
        match queryProcessGroup pid with
        | ProcessGroupQuery.Found pgid -> Some pgid
        | ProcessGroupQuery.Absent
        | ProcessGroupQuery.Uncertain -> None
