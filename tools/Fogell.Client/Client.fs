namespace Fogell.Client

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Fogell.Domain

module Client =
    exception private Refusal of int * string
    let private refuse code message = raise (Refusal(code, message))

    type private Options =
        { Command: string; BaseUri: Uri; Organization: Guid; Project: Guid
          Build: Guid option; TokenFile: string; Pipeline: string option; Snapshot: string option; Key: string option
          From: int; RequestSeconds: int; WatchSeconds: int; PollMs: int; MaxBytes: int }

    let private parse (args: string array) =
        if args.Length = 0 then refuse 2 "command_required"
        let command = args[0]
        let commands = set [ "submit"; "status"; "logs"; "feedback"; "watch"; "cancel"; "source" ]
        if not (commands.Contains command) then refuse 2 "unknown_command"
        let common = set [ "url"; "organization"; "project"; "token-file"; "request-timeout-seconds"; "max-response-bytes" ]
        let specific =
            match command with
            | "submit" -> set [ "pipeline"; "snapshot"; "idempotency-key" ]
            | "watch" -> set [ "build"; "from"; "watch-timeout-seconds"; "poll-interval-ms" ]
            | "logs" | "feedback" -> set [ "build"; "from" ]
            | _ -> set [ "build" ]
        let rec pairs index values =
            if index = args.Length then values
            elif index + 1 >= args.Length || not (args[index].StartsWith "--") then refuse 2 "invalid_options"
            else
                let key = args[index].Substring 2
                if not ((Set.union common specific).Contains key) || Map.containsKey key values then refuse 2 "invalid_options"
                pairs (index + 2) (Map.add key args[index + 1] values)
        let values = pairs 1 Map.empty
        let required key =
            match Map.tryFind key values with
            | Some value when not (String.IsNullOrWhiteSpace value) -> value
            | _ -> refuse 2 ("missing_" + key)
        let number key defaultValue minimum maximum =
            match Map.tryFind key values with
            | None -> defaultValue
            | Some value ->
                match Int32.TryParse value with
                | true, parsed when parsed >= minimum && parsed <= maximum -> parsed
                | _ -> refuse 2 ("invalid_" + key)
        let identifier key =
            match Guid.TryParse(required key) with
            | true, parsed -> parsed
            | _ -> refuse 2 ("invalid_" + key)
        let uri =
            match Uri.TryCreate(required "url", UriKind.Absolute) with
            | true, uri -> uri
            | _ -> refuse 2 "invalid_url"
        let loopback =
            // Deliberately allow only canonical literal loopback, never DNS names
            // or alternate IP spellings that URL parsers can normalize.
            let authority = (required "url").Split('/')[2]
            let literal = authority.Split(':')[0]
            (literal = "127.0.0.1" || authority = "[::1]" || authority.StartsWith("[::1]:", StringComparison.Ordinal))
            && uri.IsLoopback
        if (uri.Scheme <> "https" && not (uri.Scheme = "http" && loopback))
           || uri.UserInfo <> "" || uri.Query <> "" || uri.Fragment <> "" then refuse 2 "unsafe_url"
        if command = "submit" && (Map.containsKey "pipeline" values = Map.containsKey "snapshot" values) then refuse 2 "choose_pipeline_or_snapshot"
        let key = if command = "submit" then Some(required "idempotency-key") else None
        match key with
        | Some value when Encoding.UTF8.GetByteCount value > 256 || value |> Seq.exists Char.IsControl -> refuse 2 "invalid_idempotency_key"
        | _ -> ()
        { Command = command; BaseUri = Uri(uri.AbsoluteUri.TrimEnd('/') + "/")
          Organization = identifier "organization"; Project = identifier "project"
          Build = if command = "submit" then None else Some(identifier "build")
          TokenFile = required "token-file"; Pipeline = Map.tryFind "pipeline" values; Snapshot = Map.tryFind "snapshot" values
          Key = key; From = number "from" 0 0 Int32.MaxValue
          RequestSeconds = number "request-timeout-seconds" 30 1 3600
          WatchSeconds = number "watch-timeout-seconds" 300 1 86400
          PollMs = number "poll-interval-ms" 250 1 60000
          MaxBytes = number "max-response-bytes" 1048576 1 SourceSnapshot.MaxEnvelopeBytes }

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openNonblocking(string path, int flags)

    let private openInput errorCode path =
        if OperatingSystem.IsLinux() then
            // O_NONBLOCK avoids hanging before we can refuse a FIFO/device.
            let descriptor = openNonblocking(path, 0x800 ||| 0x80000)
            if descriptor < 0 then refuse 2 errorCode
            let handle = new SafeFileHandle(nativeint descriptor, true)
            try
                let stream = new FileStream(handle, FileAccess.Read)
                if not stream.CanSeek then
                    stream.Dispose()
                    refuse 2 errorCode
                stream
            with _ -> handle.Dispose(); reraise()
        else File.OpenRead path

    let createHttpClient () =
        let handler = new HttpClientHandler(AllowAutoRedirect = false, UseCookies = false)
        new HttpClient(handler, true, Timeout = Timeout.InfiniteTimeSpan)

    let private readBounded (stream: Stream) maxBytes (ct: CancellationToken) = task {
        use bytes = new MemoryStream()
        let buffer = Array.zeroCreate<byte> (min 8192 (maxBytes + 1))
        let mutable finished = false
        while not finished do
            let! count = stream.ReadAsync(buffer.AsMemory(0, min buffer.Length (maxBytes + 1 - int bytes.Length)), ct)
            if count = 0 then finished <- true
            else
                bytes.Write(buffer, 0, count)
                if bytes.Length > int64 maxBytes then refuse 3 "response_too_large"
        return bytes.ToArray()
    }

    let private field (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value -> value
        | _ -> refuse 3 "malformed_response"
    let private str root name = (field root name).GetString()
    let private integer root name = (field root name).GetInt32()
    let private boolean root name = (field root name).GetBoolean()
    let private validateId root name expected =
        match Guid.TryParse(str root name) with
        | true, actual when expected |> Option.forall ((=) actual) -> ()
        | _ -> refuse 3 "identity_mismatch"
    let private terminal status =
        match status with
        | "success" | "succeeded" | "failure" | "failed" | "unstable" | "aborted" -> true
        | "queued" | "running" | "not_built" | "reconciliation_required" -> false
        | _ -> refuse 3 "unknown_status"

    let private validatePage root build cursor feedback =
        validateId root "build_id" (Some build)
        if integer root "from_sequence" <> cursor then refuse 3 "cursor_mismatch"
        let next = integer root "next_sequence"
        let chunks = field root "chunks"
        if chunks.ValueKind <> JsonValueKind.Array then refuse 3 "malformed_response"
        let mutable expectedNext = cursor
        let mutable truncated = false
        for chunk in chunks.EnumerateArray() do
            let sequence = integer chunk "sequence"
            if sequence < expectedNext || sequence = Int32.MaxValue then refuse 3 "invalid_sequence"
            if isNull (str chunk "body") then refuse 3 "malformed_response"
            if feedback then truncated <- boolean chunk "truncated" || truncated
            expectedNext <- sequence + 1
        if next <> expectedNext then refuse 3 "invalid_cursor"
        if feedback then
            if integer root "schema_version" <> 1 then refuse 3 "unsupported_schema"
            let status = str root "status"
            if terminal status <> boolean root "is_terminal" then refuse 3 "inconsistent_terminal"
            boolean root "cancellation_requested" |> ignore
            if boolean root "truncated" <> truncated then refuse 3 "inconsistent_truncation"
            if boolean root "has_more" && next = cursor then refuse 3 "nonprogressing_page"
            let mutable identity = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("source_identity", &identity) && identity.ValueKind <> JsonValueKind.Null then
                if integer identity "schema_version" <> 1 || str identity "kind" <> "content_snapshot" then refuse 3 "invalid_source_identity"
                for name in [ "snapshot_sha256"; "pipeline_sha256"; "manifest_sha256" ] do
                    let digest = str identity name
                    if isNull digest || digest.Length <> 64 || digest |> Seq.exists (fun c -> not (Char.IsAsciiHexDigit c)) then refuse 3 "invalid_source_identity"
                validateId identity "parent_loop_id" None
                let verification = str identity "verification_state"
                if verification <> "pending" && verification <> "verified" then refuse 3 "invalid_source_verification"
                if verification = "verified" then
                    validateId identity "attempt_id" None
                    let observed = str identity "run_host_sha256"
                    if isNull observed || observed.Length <> 64 || observed |> Seq.exists (fun c -> not (Char.IsAsciiHexDigit c)) then refuse 3 "invalid_source_identity"
                    let expected = str identity "expected_tool_sha256"
                    if not (isNull expected) && expected <> observed then refuse 3 "source_tool_mismatch"
                elif status = "success" || status = "succeeded" then refuse 3 "source_not_verified"
        next

    let private emit (output: TextWriter) elapsed (root: JsonElement) =
        let record = JsonNode.Parse(root.GetRawText()).AsObject()
        record["client_elapsed_ms"] <- JsonValue.Create<int64>(elapsed)
        output.WriteLine(record.ToJsonString())
        output.Flush()

    let private requestBytes (http: HttpClient) (options: Options) (token: string) (path: string) (methodName: HttpMethod) (body: byte array option) (key: string option) (ct: CancellationToken) = task {
        use deadline = CancellationTokenSource.CreateLinkedTokenSource ct
        deadline.CancelAfter(TimeSpan.FromSeconds(float options.RequestSeconds))
        use request = new HttpRequestMessage(methodName, Uri(options.BaseUri, path))
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
        match key with Some value -> request.Headers.Add("Idempotency-Key", value) | None -> ()
        match body with
        | Some bytes ->
            request.Content <- new ByteArrayContent(bytes)
            request.Content.Headers.ContentType <- MediaTypeHeaderValue((if options.Snapshot.IsSome then SourceSnapshot.MediaType else "text/plain"), CharSet = "utf-8")
        | None -> ()
        use! response = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
        if response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > int64 options.MaxBytes then
            refuse 3 "response_too_large"
        use! stream = response.Content.ReadAsStreamAsync(deadline.Token)
        let! bytes = readBounded stream options.MaxBytes deadline.Token
        if not response.IsSuccessStatusCode then
            // Preserve stable refusal codes but never echo arbitrary response text,
            // URLs, request headers, credentials, or exception messages.
            let mutable code = "http_error"
            try
                use error = JsonDocument.Parse bytes
                let value = str error.RootElement "code"
                if not (isNull value) && value.Length <= 100 && not (value.Contains(token, StringComparison.Ordinal))
                   && value |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || c = '_' || Char.IsAsciiDigit c) then code <- value
            with _ -> ()
            refuse 3 ($"{code}: HTTP {int response.StatusCode}")
        return bytes
    }

    let private request http options token path methodName body key ct = task {
        let! bytes = requestBytes http options token path methodName body key ct
        return JsonDocument.Parse bytes
    }

    let private runOnline (http: HttpClient) (output: TextWriter) (errors: TextWriter) args = task {
        try
            let options = parse args
            use tokenStream = openInput "invalid_token_file" options.TokenFile
            if tokenStream.Length > 4096L then refuse 2 "invalid_token_file"
            use tokenDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(float options.RequestSeconds))
            let! tokenBytes = readBounded tokenStream 4096 tokenDeadline.Token
            let token = UTF8Encoding(false, true).GetString(tokenBytes).TrimEnd('\r', '\n')
            if String.IsNullOrWhiteSpace token || token |> Seq.exists (fun c -> Char.IsWhiteSpace c || Char.IsControl c) then refuse 2 "invalid_token_file"
            let path = $"api/v1/organizations/{options.Organization}/projects/{options.Project}/builds"
            let buildPath = match options.Build with Some build -> $"{path}/{build}" | None -> path
            let stopwatch = Stopwatch.StartNew()
            use watchDeadline = new CancellationTokenSource()
            if options.Command = "watch" then watchDeadline.CancelAfter(TimeSpan.FromSeconds(float options.WatchSeconds))
            let get suffix = request http options token (buildPath + suffix) HttpMethod.Get None None watchDeadline.Token
            match options.Command with
            | "watch" ->
                let mutable cursor = options.From
                let mutable result = None
                while result.IsNone do
                    watchDeadline.Token.ThrowIfCancellationRequested()
                    use! response = get ($"/feedback?from={cursor}")
                    let root = response.RootElement
                    cursor <- validatePage root options.Build.Value cursor true
                    emit output stopwatch.ElapsedMilliseconds root
                    let status = str root "status"
                    if status = "reconciliation_required" then result <- Some 5
                    elif boolean root "is_terminal" && not (boolean root "has_more") then
                        result <- Some(if status = "success" || status = "succeeded" then 0 else 1)
                    elif not (boolean root "has_more") then
                        do! Task.Delay(options.PollMs, watchDeadline.Token)
                return result.Value
            | "source" ->
                let! bytes = requestBytes http options token (buildPath + "/source") HttpMethod.Get None None CancellationToken.None
                match SourceSnapshot.decode bytes with
                | Ok(Some _) -> ()
                | _ -> refuse 3 "invalid_source_snapshot"
                output.Write(UTF8Encoding(false, true).GetString bytes)
                output.Flush()
                return 0
            | "submit" ->
                let sourcePath = options.Snapshot |> Option.orElse options.Pipeline |> Option.get
                let sourceLimit = if options.Snapshot.IsSome then SourceSnapshot.MaxEnvelopeBytes else SourceSnapshot.MaxContentBytes
                use source = openInput "invalid_pipeline_file" sourcePath
                if source.Length > int64 sourceLimit then refuse 2 "pipeline_too_large"
                use sourceDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(float options.RequestSeconds))
                let! bytes = readBounded source sourceLimit sourceDeadline.Token
                if options.Snapshot.IsSome then
                    match SourceSnapshot.decode bytes with
                    | Ok(Some _) -> ()
                    | _ -> refuse 2 "invalid_source_snapshot"
                use! response = request http options token path HttpMethod.Post (Some bytes) options.Key CancellationToken.None
                for name in [ "build_id"; "attempt_id"; "node_id" ] do validateId response.RootElement name None
                integer response.RootElement "number" |> ignore
                boolean response.RootElement "was_existing" |> ignore
                emit output stopwatch.ElapsedMilliseconds response.RootElement
                return 0
            | command ->
                let! response =
                    match command with
                    | "status" -> get ""
                    | "logs" -> get ($"/logs?from={options.From}")
                    | "feedback" -> get ($"/feedback?from={options.From}")
                    | _ -> request http options token (buildPath + "/cancel") HttpMethod.Post None None CancellationToken.None
                use response = response
                let root = response.RootElement
                match command with
                | "logs" | "feedback" -> validatePage root options.Build.Value options.From (command = "feedback") |> ignore
                | "status" ->
                    validateId root "build_id" options.Build
                    terminal (str root "status") |> ignore
                    boolean root "cancellation_requested" |> ignore
                | _ ->
                    if not (boolean root "accepted") then refuse 3 "cancellation_not_accepted"
                    boolean root "already_requested" |> ignore
                emit output stopwatch.ElapsedMilliseconds root
                return 0
        with
        | Refusal(code, message) -> errors.WriteLine($"{{\"error\":\"{message}\"}}"); return code
        | :? OperationCanceledException -> errors.WriteLine("{\"error\":\"client_timeout\"}"); return 4
        | :? IOException | :? UnauthorizedAccessException -> errors.WriteLine("{\"error\":\"local_io_failure\"}"); return 3
        | :? HttpRequestException -> errors.WriteLine("{\"error\":\"transport_failure\"}"); return 3
        | _ -> errors.WriteLine("{\"error\":\"malformed_response_or_input\"}"); return 3
    }

    let run (http: HttpClient) (output: TextWriter) (errors: TextWriter) (args: string array) =
        if args.Length > 0 && args[0] = "snapshot" then
            Task.FromResult(SnapshotCommand.run output errors args)
        elif args.Length > 0 && args[0] = "tool-identity" then
            Task.FromResult(SnapshotCommand.toolIdentity output errors args)
        else runOnline http output errors args
