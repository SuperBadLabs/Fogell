namespace Fogell.Client

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open Microsoft.Win32.SafeHandles
open Fogell.Domain

module SnapshotCommand =
    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openInput(string path, int flags)

    let private readFile path maxBytes =
        let descriptor = openInput(path, 0x800 ||| 0x20000 ||| 0x80000) // NONBLOCK | NOFOLLOW | CLOEXEC
        if descriptor < 0 then invalidOp "snapshot_file_unavailable"
        use handle = new SafeFileHandle(nativeint descriptor, true)
        use stream = new FileStream(handle, FileAccess.Read)
        if not stream.CanSeek || stream.Length > int64 maxBytes then invalidOp "snapshot_file_invalid_or_too_large"
        use bytes = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 8192
        let mutable finished = false
        while not finished do
            let count = stream.Read(buffer, 0, min buffer.Length (maxBytes + 1 - int bytes.Length))
            if count = 0 then finished <- true
            else
                bytes.Write(buffer, 0, count)
                if bytes.Length > int64 maxBytes then invalidOp "snapshot_file_too_large"
        bytes.ToArray()

    let run (output: TextWriter) (errors: TextWriter) (args: string array) =
        try
            if not (OperatingSystem.IsLinux()) then invalidOp "snapshot_requires_linux"
            let allowed = set [ "--pipeline"; "--source-root"; "--files-from"; "--output"; "--parent-loop"; "--tool-sha256" ]
            if args.Length <> 11 && args.Length <> 13 then invalidOp "snapshot_options_required"
            let mutable options = Map.empty
            for offset in 1 .. 2 .. args.Length - 1 do
                if not (allowed.Contains args[offset]) || options.ContainsKey args[offset] then invalidOp "invalid_snapshot_options"
                options <- options.Add(args[offset], args[offset + 1])
            let required name = match options.TryFind name with Some value -> value | None -> invalidOp "snapshot_options_required"
            let root = Path.GetFullPath(required "--source-root")
            if not (Directory.Exists root) || not (isNull (DirectoryInfo root).LinkTarget) then invalidOp "invalid_source_root"
            let pipeline = readFile (required "--pipeline") SourceSnapshot.MaxContentBytes
            let inventory = readFile (required "--files-from") (SourceSnapshot.MaxFiles * 514)
            let paths = UTF8Encoding(false, true).GetString(inventory).Split('\n') |> Array.map _.TrimEnd('\r') |> Array.filter ((<>) "")
            if paths.Length = 0 || paths.Length > SourceSnapshot.MaxFiles then invalidOp "invalid_source_file_count"
            let mutable total = pipeline.Length
            let files =
                paths |> Array.map (fun relative ->
                    if not (SourceSnapshot.validPath relative) then invalidOp "invalid_source_path"
                    let mutable path = root
                    for segment in relative.Split('/') do
                        path <- Path.Combine(path, segment)
                        if not (isNull (FileInfo path).LinkTarget) then invalidOp "snapshot_symlink_refused"
                    let bytes = readFile path (SourceSnapshot.MaxContentBytes - total)
                    total <- total + bytes.Length
                    { Path = relative; ContentBase64 = Convert.ToBase64String bytes
                      Sha256 = SourceSnapshot.digest bytes
                      Executable = (File.GetUnixFileMode path &&& UnixFileMode.UserExecute) <> enum<UnixFileMode> 0 })
                |> Array.sortBy _.Path
            let snapshot =
                { SchemaVersion = 1; Kind = "fogell.source_snapshot"
                  PipelineBase64 = Convert.ToBase64String pipeline
                  PipelineSha256 = SourceSnapshot.digest pipeline
                  SnapshotSha256 = SourceSnapshot.treeDigest files
                  ParentLoopId = required "--parent-loop"
                  ExpectedToolSha256 = options.TryFind "--tool-sha256" |> Option.defaultValue null
                  Environment = SourceSnapshot.currentEnvironment (); Files = files }
                |> SourceSnapshot.validate
            let bytes = SourceSnapshot.serialize snapshot
            if bytes.Length > SourceSnapshot.MaxEnvelopeBytes then invalidOp "source_snapshot_too_large"
            let destination = Path.GetFullPath(required "--output")
            use target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            File.SetUnixFileMode(destination, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            target.Write bytes
            target.Flush true
            output.WriteLine((SourceSnapshot.identity snapshot "unverified" None).GetRawText())
            0
        with
        | :? InvalidOperationException as error ->
            errors.WriteLine(JsonSerializer.Serialize {| error = error.Message |}); 2
        | _ -> errors.WriteLine("{\"error\":\"snapshot_input_or_output_failure\"}"); 2

    let toolIdentity (output: TextWriter) (errors: TextWriter) (args: string array) =
        try
            if args.Length <> 3 || args[1] <> "--run-host" then invalidOp "tool_identity_options_required"
            let hash = SourceSnapshot.runtimeToolDigest args[2]
            output.WriteLine(JsonSerializer.Serialize {| run_host_sha256 = hash; observed_runtime_version = Environment.Version.ToString() |})
            0
        with _ -> errors.WriteLine("{\"error\":\"tool_identity_failed\"}"); 2
