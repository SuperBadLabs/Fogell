namespace Fogell.Domain

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<CLIMutable>]
type SourceEnvironment = { Os: string; Architecture: string; DotnetMajor: int }
[<CLIMutable>]
type SourceFile = { Path: string; ContentBase64: string; Sha256: string; Executable: bool }
[<CLIMutable>]
type SourceSnapshot =
    { SchemaVersion: int
      Kind: string
      PipelineBase64: string
      PipelineSha256: string
      SnapshotSha256: string
      ParentLoopId: string
      ExpectedToolSha256: string
      Environment: SourceEnvironment
      Files: SourceFile array }

/// Explicit content snapshots are the verified identity. They never assert a
/// branch name, clean Git checkout or repository commit, even if packed inside
/// a Git working tree. Authentication and process environment are not serialized.
module SourceSnapshot =
    [<Literal>]
    let MaxContentBytes = 16777216
    [<Literal>]
    let MaxEnvelopeBytes = 25165824
    [<Literal>]
    let MaxFiles = 4096
    [<Literal>]
    let MediaType = "application/vnd.fogell.submission.v1+json"
    let private settings = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                                                UnmappedMemberHandling = Serialization.JsonUnmappedMemberHandling.Disallow)
    let private utf8 = UTF8Encoding(false, true)
    let digest (bytes: byte array) = Convert.ToHexStringLower(SHA256.HashData bytes)
    let serialize (value: SourceSnapshot) = JsonSerializer.SerializeToUtf8Bytes(value, settings)
    let currentEnvironment () =
        { Os = if OperatingSystem.IsLinux() then "linux" else "unsupported"
          Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
          DotnetMajor = Environment.Version.Major }

    let validPath (path: string) =
        if String.IsNullOrWhiteSpace path || path.Length > 512 || path.StartsWith('/')
           || path.Contains('\\') || path.Contains(':') || path |> Seq.exists Char.IsControl then false
        else
            let segments = path.Split('/')
            segments |> Array.forall (fun segment ->
                let lower = segment.ToLowerInvariant()
                segment <> "" && not (segment.StartsWith('.'))
                && not (List.contains lower [ "bin"; "obj"; "evidence"; "token"; "token.txt"; "credentials"; "credentials.json"; "secrets"; "secrets.json"; "id_rsa"; "id_ed25519" ])
                && not (List.exists (fun (suffix: string) -> lower.EndsWith(suffix, StringComparison.Ordinal)) [ ".pem"; ".pfx"; ".p12"; ".key"; ".token" ]))

    let private content (encoded: string) =
        if isNull encoded || encoded.Length > ((MaxContentBytes + 2) / 3) * 4 then invalidOp "source_snapshot_too_large"
        Convert.FromBase64String encoded

    let treeDigest (files: SourceFile array) =
        files
        |> Array.sortBy _.Path
        |> Array.map (fun file ->
            let executable = if file.Executable then "1" else "0"
            $"{file.Path}\000{file.Sha256}\000{executable}\n")
        |> String.concat ""
        |> utf8.GetBytes
        |> digest

    let private checkNoDuplicateProperties (root: JsonElement) =
        let rec walk (element: JsonElement) =
            if element.ValueKind = JsonValueKind.Object then
                let names = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                for property in element.EnumerateObject() do
                    if not (names.Add property.Name) then invalidOp "duplicate_source_property"
                    walk property.Value
            elif element.ValueKind = JsonValueKind.Array then
                for value in element.EnumerateArray() do walk value
        walk root

    let validate (value: SourceSnapshot) =
        if value.SchemaVersion <> 1 || value.Kind <> "fogell.source_snapshot" then invalidOp "invalid_source_schema"
        if isNull (box value.Environment) || value.Environment.Os <> "linux"
           || not (List.contains value.Environment.Architecture [ "x64"; "arm64" ])
           || value.Environment.DotnetMajor <> 10 then invalidOp "invalid_source_environment"
        match Guid.TryParse value.ParentLoopId with
        | true, identifier when identifier <> Guid.Empty -> ()
        | _ -> invalidOp "invalid_parent_loop_id"
        if not (isNull value.ExpectedToolSha256) &&
           (value.ExpectedToolSha256.Length <> 64 || value.ExpectedToolSha256 |> Seq.exists (fun c -> not ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))) then
            invalidOp "invalid_expected_tool_digest"
        let pipeline = content value.PipelineBase64
        if pipeline.Length = 0 || digest pipeline <> value.PipelineSha256 then invalidOp "pipeline_digest_mismatch"
        utf8.GetString pipeline |> ignore
        if isNull value.Files || value.Files.Length = 0 || value.Files.Length > MaxFiles then invalidOp "invalid_source_file_count"
        let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let mutable total = int64 pipeline.Length
        for file in value.Files do
            if isNull (box file) || not (validPath file.Path) || not (seen.Add file.Path) then invalidOp "invalid_source_path"
            let bytes = content file.ContentBase64
            total <- total + int64 bytes.Length
            if total > int64 MaxContentBytes then invalidOp "source_snapshot_too_large"
            if digest bytes <> file.Sha256 then invalidOp "source_digest_mismatch"
        // A file cannot also be the parent directory of another admitted file.
        for file in value.Files do
            let components = file.Path.Split('/')
            for count in 1 .. components.Length - 1 do
                if seen.Contains(String.Join('/', components[0 .. count - 1])) then invalidOp "source_path_collision"
        if treeDigest value.Files <> value.SnapshotSha256 then invalidOp "snapshot_digest_mismatch"
        value

    let decode (bytes: byte array) : Result<SourceSnapshot option, string> =
        try
            if isNull bytes || bytes.Length > MaxEnvelopeBytes then Error "source_snapshot_too_large"
            elif not (utf8.GetString(bytes).TrimStart().StartsWith('{')) then Ok None
            else
                use document = JsonDocument.Parse bytes
                let mutable kind = Unchecked.defaultof<JsonElement>
                if not (document.RootElement.TryGetProperty("kind", &kind)) then Ok None
                else
                    checkNoDuplicateProperties document.RootElement
                    let value = JsonSerializer.Deserialize<SourceSnapshot>(bytes, settings) |> validate
                    Ok(Some value)
        with
        | :? InvalidOperationException as error -> Error error.Message
        | _ -> Error "malformed_source_snapshot"

    let pipeline bytes =
        match decode bytes with
        | Error error -> Error error
        | Ok None -> Ok bytes
        | Ok(Some snapshot) -> Ok(content snapshot.PipelineBase64)

    let verifyEnvironment snapshot =
        if snapshot.Environment <> currentEnvironment () then Error "source_environment_mismatch" else Ok ()

    /// Bind the configured entry executable and its local managed/native runtime
    /// closure without recording machine paths or process environment values.
    let runtimeToolDigest (executable: string) =
        let full = Path.GetFullPath executable
        let root = Path.GetDirectoryName full
        let files =
            Array.append [| full |] (Directory.GetFiles root)
            |> Array.filter (fun path ->
                path = full || path.EndsWith(".dll", StringComparison.Ordinal)
                || path.EndsWith(".so", StringComparison.Ordinal)
                || path.EndsWith(".deps.json", StringComparison.Ordinal)
                || path.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
            |> Array.distinct
            |> Array.sortBy Path.GetFileName
        if files.Length > 256 then invalidOp "source_tool_closure_too_large"
        let mutable total = 0L
        let hashes =
            files |> Array.map (fun path ->
                let info = FileInfo path
                total <- total + info.Length
                if total > 268435456L || not (isNull info.LinkTarget) then invalidOp "source_tool_closure_invalid"
                use stream = File.OpenRead path
                let hash = Convert.ToHexStringLower(SHA256.HashData stream)
                Path.GetFileName(path) + "\000" + hash + "\n")
        String.concat "" hashes |> utf8.GetBytes |> digest

    let identity (snapshot: SourceSnapshot) verificationState (runHostSha256: string option) =
        JsonSerializer.SerializeToElement(
            {| schema_version = 1
               kind = "content_snapshot"
               verification_state = verificationState
               snapshot_sha256 = snapshot.SnapshotSha256
               pipeline_sha256 = snapshot.PipelineSha256
               manifest_sha256 = digest (serialize snapshot)
               parent_loop_id = snapshot.ParentLoopId
               expected_tool_sha256 = snapshot.ExpectedToolSha256
               file_count = snapshot.Files.Length
               environment = {| os = snapshot.Environment.Os; architecture = snapshot.Environment.Architecture; dotnet_major = snapshot.Environment.DotnetMajor |}
               run_host_sha256 = runHostSha256 |> Option.defaultValue null
               observed_runtime_version = if runHostSha256.IsSome then Environment.Version.ToString() else null |})

    /// Caller supplies a freshly prepared, controller-owned workspace. Every
    /// admitted path is relative and parent links are refused before writing.
    /// On failure no pipeline step may execute; the partial workspace is disposable.
    let materialize (snapshot: SourceSnapshot) workspace =
        try
            validate snapshot |> ignore
            match verifyEnvironment snapshot with Error error -> invalidOp error | Ok () -> ()
            let root = Path.GetFullPath workspace
            if not (Directory.Exists root) || not (isNull (DirectoryInfo root).LinkTarget) then invalidOp "invalid_source_workspace"
            for file in snapshot.Files do
                let components = file.Path.Split('/')
                let mutable directory = root
                for segment in components[0 .. components.Length - 2] do
                    directory <- Path.Combine(directory, segment)
                    if not (isNull (DirectoryInfo directory).LinkTarget) then invalidOp "source_parent_link"
                    Directory.CreateDirectory directory |> ignore
                let target = Path.Combine(directory, components[components.Length - 1])
                use stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                let bytes = content file.ContentBase64
                stream.Write bytes
                stream.Flush(true)
                stream.Close()
                if OperatingSystem.IsLinux() then
                    File.SetUnixFileMode(target, if file.Executable then enum<UnixFileMode> 0o755 else enum<UnixFileMode> 0o644)
                if digest (File.ReadAllBytes target) <> file.Sha256 then invalidOp "materialized_source_mismatch"
            Ok ()
        with
        | :? InvalidOperationException as error -> Error error.Message
        | _ -> Error "source_materialization_failed"
