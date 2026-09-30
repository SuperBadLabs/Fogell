namespace Fogell.Retention

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open Microsoft.Win32.SafeHandles

type Entry =
    { Path: string; Identity: string; Kind: int; Bytes: int64; Version: string }

type Manifest =
    { RootIdentity: string
      Ancestors: Entry array
      Entries: Entry array
      MissingRoots: string array
      Roots: string array
      Cursor: int
      PendingDelete: bool
      Nonce: string
      Bytes: int64 }

/// Linux descriptor-relative inventory and unlink. No recursive pathname delete.
module Filesystem =
    [<DllImport("libc", EntryPoint="openat", SetLastError=true)>]
    extern int private openat(int parent, string path, int flags)
    [<DllImport("libc", EntryPoint="statx", SetLastError=true)>]
    extern int private statx(int parent, string path, int flags, uint32 mask, nativeint buffer)
    [<DllImport("libc", EntryPoint="unlinkat", SetLastError=true)>]
    extern int private unlinkat(int parent, string path, int flags)
    [<DllImport("libc", EntryPoint="fsync", SetLastError=true)>]
    extern int private fsync(int descriptor)
    [<DllImport("libc", EntryPoint="renameat2", SetLastError=true)>]
    extern int private renameat2(int oldParent, string oldName, int newParent, string newName, uint32 flags)

    let private fail reason = raise (IOException reason)
    let private fd (handle: SafeFileHandle) = int(handle.DangerousGetHandle())
    let private directoryFlags = 0x10000 ||| 0x20000 ||| 0x80000 ||| 0x800
    let private openDirectory parent name =
        let result = openat(parent, name, directoryFlags)
        if result < 0 then fail $"directory_open_refused:{Marshal.GetLastPInvokeError()}"
        new SafeFileHandle(nativeint result, true)

    let private status parent name relative =
        let buffer = Marshal.AllocHGlobal 256
        try
            let flags = if name = "" then 0x1000 else 0x100
            if statx(parent, name, flags, 0x1fffu, buffer) <> 0 then
                if Marshal.GetLastPInvokeError() = 2 then None
                else fail $"identity_unavailable:{Marshal.GetLastPInvokeError()}"
            else
                let u32 offset = uint32(Marshal.ReadInt32(buffer,offset))
                let u64 offset = uint64(Marshal.ReadInt64(buffer,offset))
                // Btime prevents an inode recycled after unlink from becoming
                // the identity of the earlier journaled entry.
                if u32 0 &&& 0x1bc7u <> 0x1bc7u then fail "identity_fields_unavailable"
                let kind = int(Marshal.ReadInt16(buffer,28)) &&& 0xf000
                if kind <> 0x4000 && kind <> 0x8000 && kind <> 0xa000 then fail "special_file_refused"
                if kind = 0x8000 && u32 16 <> 1u then fail "hardlinked_file_refused"
                let identity = $"{u32 136}:{u32 140}:{u64 144}:{u64 32}:{u64 80}:{u32 88}"
                let version = $"{u64 96}:{u32 104}:{u64 112}:{u32 120}"
                let size = u64 40
                if size > uint64 Int64.MaxValue then fail "file_size_overflow"
                Some { Path=relative; Identity=identity; Kind=kind
                       Bytes=(if kind=0x4000 then 0L else int64 size); Version=version }
        finally Marshal.FreeHGlobal buffer

    let private required parent name relative =
        status parent name relative |> Option.defaultWith(fun () -> fail "expected_path_missing")

    let private same (expected: Entry) (actual: Entry) =
        expected.Identity = actual.Identity && expected.Kind = actual.Kind &&
        (expected.Kind = 0x4000 || (expected.Bytes = actual.Bytes && expected.Version = actual.Version))

    let private sameMoved (expected: Entry) (actual: Entry) =
        // rename updates ctime; mtime and content length must remain unchanged.
        same { expected with Version=actual.Version } actual &&
        (expected.Kind=0x4000 || expected.Version.Split(':')[2..] = actual.Version.Split(':')[2..])

    let openRoot (root: string) =
        if not (OperatingSystem.IsLinux()) || RuntimeInformation.ProcessArchitecture <> Architecture.X64 then
            fail "retention_requires_linux_x64"
        if not (Path.IsPathFullyQualified root) then fail "absolute_state_root_required"
        let mutable current = openDirectory -100 "/"
        try
            for part in Path.GetFullPath(root).Split('/',StringSplitOptions.RemoveEmptyEntries) do
                let next = openDirectory (fd current) part
                current.Dispose()
                current <- next
            current
        with _ -> current.Dispose(); reraise()

    let private rootIdentity (root: SafeFileHandle) = (required (fd root) "" "").Identity

    let private parts (relative: string) =
        let segments = relative.Split('/')
        if segments.Length > 64 || (segments |> Array.exists(fun p -> p="" || p="." || p="..")) then fail "unsafe_relative_path"
        segments

    let private parent (root: SafeFileHandle) relative (expected: Map<string,Entry>) collect =
        let segments = parts relative
        let mutable current = openDirectory (fd root) "."
        let mutable prefix = ""
        try
            for part in segments |> Array.take(segments.Length-1) do
                prefix <- if prefix="" then part else prefix+"/"+part
                let next = openDirectory (fd current) part
                let actual = required (fd next) "" prefix
                match Map.tryFind prefix expected with
                | Some prior when not(same prior actual) -> next.Dispose(); fail "ancestor_identity_changed"
                | _ -> ()
                collect actual
                current.Dispose()
                current <- next
            current, Array.last segments
        with _ -> current.Dispose(); reraise()

    let inventory rootPath paths maximumEntries (checkBudget: unit -> unit) =
        use root = openRoot rootPath
        let entries = ResizeArray<Entry>()
        let ancestors = Collections.Generic.Dictionary<string,Entry>()
        let missing = ResizeArray<string>()
        let mutable visited = 0
        let add entry =
            if entries.Count >= maximumEntries then fail "manifest_entry_limit"
            entries.Add entry
        let collect entry = ancestors[entry.Path] <- entry
        let rec scan parentFd name relative mount =
            checkBudget()
            visited <- visited + 1
            if visited > maximumEntries then fail "manifest_entry_limit"
            parts relative |> ignore
            let entry = required parentFd name relative
            let mountKey = entry.Identity.Split(':')[2]
            if mountKey <> mount then fail "nested_mount_refused"
            if entry.Kind=0x4000 then
                use child = openDirectory parentFd name
                if not(same entry (required (fd child) "" relative)) then fail "directory_identity_changed"
                collect entry
                for path in Directory.EnumerateFileSystemEntries($"/proc/self/fd/{fd child}") do
                    scan (fd child) (Path.GetFileName path) (relative+"/"+Path.GetFileName path) mount
                if not(same entry (required parentFd name relative)) then fail "directory_identity_changed"
            add entry
        for relative in paths do
            checkBudget()
            // Absent intermediate controller directories are legitimate only
            // for an entirely missing root. An existing symlink is never absent.
            try
                let directory,name = parent root relative Map.empty collect
                use directory = directory
                match status (fd directory) name relative with
                | None -> missing.Add relative
                | Some entry ->
                    if entry.Kind=0xa000 then fail "root_symlink_refused"
                    scan (fd directory) name relative (entry.Identity.Split(':')[2])
            with :? IOException as ex when ex.Message = "directory_open_refused:2" -> missing.Add relative
        { RootIdentity=rootIdentity root; Ancestors=ancestors.Values |> Seq.toArray
          Entries=entries.ToArray(); MissingRoots=missing.ToArray(); Roots=paths |> Seq.toArray; Cursor=0; PendingDelete=false
          Nonce=Guid.NewGuid().ToString("N")
          Bytes=entries |> Seq.sumBy _.Bytes }

    let private validatedRoot rootPath manifest =
        let root = openRoot rootPath
        if rootIdentity root <> manifest.RootIdentity then root.Dispose(); fail "state_root_identity_changed"
        root

    /// Missing is accepted only after the caller durably journaled this exact
    /// pending unlink; a replacement never becomes that entry's owner.
    let removeNext rootPath (manifest: Manifest) (boundary: string -> unit) =
        if not manifest.PendingDelete then fail "delete_intent_not_durable"
        use root = validatedRoot rootPath manifest
        let entry = manifest.Entries[manifest.Cursor]
        let expected = manifest.Ancestors |> Array.map(fun e -> e.Path,e) |> Map.ofArray
        let directory,name = parent root entry.Path expected ignore
        use directory = directory
        let quarantine = $".fogell-retention-{manifest.Nonce}-{manifest.Cursor}"
        match status (fd directory) name entry.Path, status (fd directory) quarantine entry.Path with
        | Some _, Some _ -> fail "delete_location_conflict"
        | Some actual, None ->
            if not(same entry actual) then fail "entry_identity_changed"
            if renameat2(fd directory,name,fd directory,quarantine,1u) <> 0 then fail "quarantine_move_refused"
        | _ -> ()
        if fsync(fd directory) <> 0 then fail "quarantine_sync_failed"
        boundary "quarantined"
        match status (fd directory) quarantine entry.Path with
        | None -> () // Journaled pending deletion completed before interruption.
        | Some actual ->
            if not(sameMoved entry actual) then fail "quarantine_identity_changed"
            if unlinkat(fd directory,quarantine,(if entry.Kind=0x4000 then 0x200 else 0)) <> 0 then
                fail $"unlink_refused:{Marshal.GetLastPInvokeError()}"
        if fsync(fd directory) <> 0 then fail "deletion_sync_failed"

    let verifyPresent rootPath (manifest: Manifest) =
        use root = validatedRoot rootPath manifest
        let expected = manifest.Ancestors |> Array.map(fun e -> e.Path,e) |> Map.ofArray
        let entry = manifest.Entries[manifest.Cursor]
        let directory,name = parent root entry.Path expected ignore
        use directory = directory
        if not(same entry (required (fd directory) name entry.Path)) then fail "entry_identity_changed"

    let verifyMissingRoots rootPath (manifest: Manifest) =
        use root = validatedRoot rootPath manifest
        let expected = manifest.Ancestors |> Array.map(fun e -> e.Path,e) |> Map.ofArray
        for relative in manifest.Roots do
            try
                let directory,name = parent root relative expected ignore
                use directory = directory
                if status (fd directory) name relative |> Option.isSome then fail "unexpected_root_appeared"
            with :? IOException as ex when ex.Message="directory_open_refused:2" -> ()

    let verifyDefinition rootPath (manifest: Manifest) relative (digest: byte array) =
        use root=validatedRoot rootPath manifest
        let entry=manifest.Entries |> Array.tryFind(fun e->e.Path=relative)
                  |> Option.defaultWith(fun ()->fail "definition_transport_missing")
        if entry.Kind<>0x8000 || entry.Bytes>33554432L then fail "definition_transport_refused"
        let expected=manifest.Ancestors |> Array.map(fun e->e.Path,e) |> Map.ofArray
        let directory,name=parent root relative expected ignore
        use directory=directory
        let descriptor=openat(fd directory,name,0x20000 ||| 0x80000 ||| 0x800)
        if descriptor<0 then fail "definition_transport_unavailable"
        use handle=new SafeFileHandle(nativeint descriptor,true)
        if not(same entry (required descriptor "" relative)) then fail "definition_transport_changed"
        use stream=new FileStream(handle,FileAccess.Read)
        if SHA256.HashData(stream)<>digest then fail "definition_database_disagreement"
        if not(same entry (required descriptor "" relative)) then fail "definition_transport_changed"
