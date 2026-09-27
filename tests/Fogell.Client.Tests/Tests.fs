module Fogell.Client.Tests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Fogell.Client

[<DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)>]
extern int private mkfifo(string path, uint32 mode)

let build = Guid.Parse "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
let organization = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
let project = "cccccccc-cccc-cccc-cccc-cccccccccccc"
let token = "private_test_credential"

type Handler(send: HttpRequestMessage -> CancellationToken -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    override _.SendAsync(request, ct) = send request ct

type UnknownLengthStream(bytes: byte array) =
    inherit MemoryStream(bytes)
    override _.CanSeek = false
    override _.Length = raise (NotSupportedException())

let response status body =
    new HttpResponseMessage(status, Content = new StringContent(body, Encoding.UTF8, "application/json"))

let page status fromSequence nextSequence more chunks =
    let terminal = List.contains status [ "success"; "succeeded"; "failure"; "failed"; "unstable"; "aborted" ]
    JsonSerializer.Serialize
        {| schema_version = 1
           build_id = string build
           status = status
           cancellation_requested = false
           is_terminal = terminal
           from_sequence = fromSequence
           next_sequence = nextSequence
           has_more = more
           truncated = false
           chunks = chunks |}
let chunk sequence body = {| sequence = sequence; body = body; truncated = false |}
let empty status = page status 0 0 false [||]

let invoke command extras send =
    let tokenFile = Path.GetTempFileName()
    try
        File.WriteAllText(tokenFile, token)
        use handler = new Handler(send)
        use client = new HttpClient(handler)
        use output = new StringWriter()
        use errors = new StringWriter()
        let args =
            [| yield! [| command; "--url"; "http://127.0.0.1:9000"; "--organization"; organization; "--project"; project; "--token-file"; tokenFile |]
               yield! (if command = "submit" then [||] else [| "--build"; string build |])
               yield! extras |]
        let code = Client.run client output errors args |> fun t -> t.GetAwaiter().GetResult()
        code, output.ToString(), errors.ToString()
    finally File.Delete tokenFile

let fixedResponse body _ _ = Task.FromResult(response HttpStatusCode.OK body)

[<Tests>]
let tests = testList "controller client" [
    testCase "watch drains terminal backlog with advanced cursors" <| fun _ ->
        let calls = ResizeArray<string>()
        let send (request: HttpRequestMessage) _ =
            calls.Add(request.RequestUri.Query)
            Expect.equal request.Headers.Authorization.Parameter token "token from file"
            let value =
                if calls.Count = 1 then page "success" 0 2 true [| chunk 1 "first" |]
                else page "success" 2 3 false [| chunk 2 "last" |]
            Task.FromResult(response HttpStatusCode.OK value)
        let code, output, errors = invoke "watch" [||] send
        Expect.equal code 0 errors
        Expect.sequenceEqual calls [ "?from=0"; "?from=2" ] "drains backlog"
        Expect.equal (output.Trim().Split('\n').Length) 2 "NDJSON"
        Expect.stringContains output "last" "last page emitted"
        Expect.stringContains output "client_elapsed_ms" "monotonic observation"

    testCase "watch only known authoritative success is zero" <| fun _ ->
        for status, expected in [ "success", 0; "succeeded", 0; "failure", 1; "failed", 1; "unstable", 1; "aborted", 1; "reconciliation_required", 5; "mystery", 3 ] do
            let actual, _, _ = invoke "watch" [||] (fixedResponse (empty status))
            Expect.equal actual expected status

    testCase "refuse corrupt feedback identity schema status and cursors" <| fun _ ->
        let valid = page "success" 0 2 false [| chunk 1 "output" |]
        let corruptions =
            [ valid.Replace(string build, organization)
              valid.Replace("\"schema_version\":1", "\"schema_version\":2")
              valid.Replace("\"is_terminal\":true", "\"is_terminal\":false")
              valid.Replace("\"from_sequence\":0", "\"from_sequence\":1")
              valid.Replace("\"next_sequence\":2", "\"next_sequence\":1")
              page "success" 0 2 false [| chunk 1 "a"; chunk 1 "duplicate" |]
              page "running" 0 0 true [||]
              "{}"; "not-json" ]
        for body in corruptions do
            let code, output, _ = invoke "watch" [||] (fixedResponse body)
            Expect.equal code 3 body
            Expect.equal output "" "unvalidated response never emitted"

    testCase "resume uses explicit cursor and preserves truncation" <| fun _ ->
        let body = (page "success" 17 20 false [| chunk 19 "prefix" |]).Replace("\"truncated\":false", "\"truncated\":true")
        let send (request: HttpRequestMessage) _ =
            Expect.equal request.RequestUri.Query "?from=17" "resume cursor"
            Task.FromResult(response HttpStatusCode.OK body)
        let code, output, errors = invoke "watch" [| "--from"; "17" |] send
        Expect.equal code 0 errors
        Expect.stringContains output "\"truncated\":true" "data-loss indication retained"

    testCase "snapshot success requires verified source and matching tool identity" <| fun _ ->
        let hash = String.replicate 64 "a"
        for state, observed, expected, expectedCode in
            [ "pending", null, null, 3
              "verified", hash, hash, 0
              "verified", hash, String.replicate 64 "b", 3
              "asserted", hash, hash, 3 ] do
            let identity =
                {| schema_version = 1; kind = "content_snapshot"; verification_state = state
                   snapshot_sha256 = hash; pipeline_sha256 = hash; manifest_sha256 = hash
                   parent_loop_id = string build; attempt_id = organization
                   run_host_sha256 = observed; expected_tool_sha256 = expected |}
            let body = System.Text.Json.Nodes.JsonNode.Parse(empty "success").AsObject()
            body["source_identity"] <- JsonSerializer.SerializeToNode identity
            let actual, _, _ = invoke "watch" [||] (fixedResponse (body.ToJsonString()))
            Expect.equal actual expectedCode "source authority controls successful watch"

    testCase "bounded response refusal" <| fun _ ->
        let code, output, errors = invoke "feedback" [| "--max-response-bytes"; "32" |] (fixedResponse (empty "success"))
        Expect.equal code 3 "refused oversized"
        Expect.equal output "" "no oversized output"
        Expect.stringContains errors "response_too_large" "named error"

    testCase "bounded read also rejects response without content length" <| fun _ ->
        let send _ _ =
            let content = new StreamContent(new UnknownLengthStream(Encoding.UTF8.GetBytes(String.replicate 200 "x")))
            let result = new HttpResponseMessage(HttpStatusCode.OK, Content = content)
            Expect.isFalse content.Headers.ContentLength.HasValue "unknown wire length"
            Task.FromResult result
        let code, _, errors = invoke "feedback" [| "--max-response-bytes"; "32" |] send
        Expect.equal code 3 "bounded stream"
        Expect.stringContains errors "response_too_large" "named failure"

    testCase "cancel requires valid positive acknowledgment" <| fun _ ->
        for body, expected in
            [ "{}", 3
              "{\"accepted\":false,\"already_requested\":false}", 3
              "{\"accepted\":true}", 3
              "{\"accepted\":true,\"already_requested\":false}", 0 ] do
            let code, _, errors = invoke "cancel" [||] (fixedResponse body)
            Expect.equal code expected errors

    testCase "token files are bounded and reject whitespace normalization" <| fun _ ->
        for contents, expected in [ token + "\r\n", 0; " " + token, 2; token + " ", 2; String.replicate 4097 "x", 2 ] do
            let path = Path.GetTempFileName()
            try
                File.WriteAllText(path, contents)
                use handler = new Handler(fixedResponse (empty "success"))
                use http = new HttpClient(handler)
                use output = new StringWriter()
                use errors = new StringWriter()
                let args = [| "feedback"; "--url"; "http://127.0.0.1"; "--organization"; organization; "--project"; project; "--build"; string build; "--token-file"; path |]
                let code = Client.run http output errors args |> fun t -> t.GetAwaiter().GetResult()
                Expect.equal code expected "token parsing"
            finally File.Delete path

    testCase "Linux FIFO pipeline and token are refused without waiting for writer" <| fun _ ->
        if OperatingSystem.IsLinux() then
            let path = Path.GetTempFileName()
            File.Delete path
            try
                Expect.equal (mkfifo(path, 0o600u)) 0 "fixture FIFO"
                let timer = Diagnostics.Stopwatch.StartNew()
                let code, _, _ = invoke "submit" [| "--pipeline"; path; "--idempotency-key"; "fifo-test" |] (fixedResponse "{}")
                Expect.equal code 2 "nonseekable pipeline refused"
                use handler = new Handler(fixedResponse (empty "success"))
                use http = new HttpClient(handler)
                use output = new StringWriter()
                use errors = new StringWriter()
                let args = [| "feedback"; "--url"; "http://127.0.0.1"; "--organization"; organization; "--project"; project; "--build"; string build; "--token-file"; path |]
                let tokenCode = Client.run http output errors args |> fun t -> t.GetAwaiter().GetResult()
                Expect.equal tokenCode 2 "nonseekable token refused"
                Expect.isLessThan timer.Elapsed.TotalSeconds 2.0 "did not block opening FIFOs"
            finally File.Delete path

    testCase "token never echoed in HTTP diagnostics" <| fun _ ->
        let send _ _ = Task.FromResult(response HttpStatusCode.Unauthorized ($"{{\"code\":\"{token}\",\"message\":\"{token}\"}}"))
        let code, output, errors = invoke "status" [||] send
        Expect.equal code 3 "HTTP failure"
        Expect.isFalse (errors.Contains token || output.Contains token) "no credentials in diagnostics"

    testCase "named API refusal survives without raw message" <| fun _ ->
        let send _ _ = Task.FromResult(response HttpStatusCode.Conflict "{\"code\":\"idempotency_conflict\",\"message\":\"untrusted details\"}")
        let code, _, errors = invoke "cancel" [||] send
        Expect.equal code 3 "API refusal"
        Expect.stringContains errors "idempotency_conflict" "stable code"
        Expect.isFalse (errors.Contains "untrusted") "message omitted"

    testCase "mutating transport failures are never replayed" <| fun _ ->
        let source = Path.GetTempFileName()
        try
            File.WriteAllText(source, "{\"version\":1,\"stages\":[{\"name\":\"test\",\"steps\":[{\"echo\":\"ok\"}]}]}")
            for command, extras in [ "submit", [| "--pipeline"; source; "--idempotency-key"; "explicit-key" |]; "cancel", [||] ] do
                let mutable calls = 0
                let send (request: HttpRequestMessage) _ =
                    calls <- calls + 1
                    Expect.equal request.Method HttpMethod.Post "mutation"
                    if command = "submit" then Expect.equal (request.Headers.GetValues("Idempotency-Key") |> Seq.head) "explicit-key" "explicit key"
                    Task.FromException<HttpResponseMessage>(HttpRequestException "sensitive diagnostic")
                let code, _, errors = invoke command extras send
                Expect.equal code 3 "uncertain transport"
                Expect.equal calls 1 "no implicit replay"
                Expect.isFalse (errors.Contains "sensitive") "exception sanitized"
        finally File.Delete source

    testCase "submission requires key before touching controller" <| fun _ ->
        let mutable called = false
        let send _ _ = called <- true; Task.FromResult(response HttpStatusCode.OK "{}")
        let code, _, _ = invoke "submit" [| "--pipeline"; "missing" |] send
        Expect.equal code 2 "key required"
        Expect.isFalse called "nothing submitted"

    testCase "invalid URL options rejected before credentials sent" <| fun _ ->
        // Test parser directly with a complete independent argument list.
        for url in [ "http://example.com"; "http://localhost"; "http://127.1"; "https://user:pass@example.com"; "https://example.com?q=x" ] do
            let mutable called = false
            use handler = new Handler(fun _ _ -> called <- true; Task.FromResult(response HttpStatusCode.OK "{}"))
            use http = new HttpClient(handler)
            use output = new StringWriter()
            use errors = new StringWriter()
            let args = [| "status"; "--url"; url; "--organization"; organization; "--project"; project; "--build"; string build; "--token-file"; "absent" |]
            let code = Client.run http output errors args |> fun t -> t.GetAwaiter().GetResult()
            Expect.equal code 2 url
            Expect.isFalse called "credentials not sent"

    testCase "redirect is refusal with no second request" <| fun _ ->
        let mutable calls = 0
        let send _ _ =
            calls <- calls + 1
            let result = response HttpStatusCode.Redirect "{}"
            result.Headers.Location <- Uri "https://other.example/"
            Task.FromResult result
        let code, _, _ = invoke "status" [||] send
        Expect.equal code 3 "no redirect follow"
        Expect.equal calls 1 "one request"

    testCase "watch deadline never cancels the server build" <| fun _ ->
        let methods = ResizeArray<HttpMethod>()
        let send (request: HttpRequestMessage) _ =
            methods.Add request.Method
            Task.FromResult(response HttpStatusCode.OK (empty "running"))
        let code, _, errors = invoke "watch" [| "--watch-timeout-seconds"; "1"; "--poll-interval-ms"; "20" |] send
        Expect.equal code 4 errors
        Expect.isTrue (methods.Count > 1) "polling occurred"
        Expect.isTrue (methods |> Seq.forall ((=) HttpMethod.Get)) "no implicit cancel"

    testCase "request deadline covers stalled request" <| fun _ ->
        let send _ ct = task {
            do! Task.Delay(Timeout.Infinite, ct)
            return response HttpStatusCode.OK "{}"
        }
        let code, _, errors = invoke "status" [| "--request-timeout-seconds"; "1" |] send
        Expect.equal code 4 errors
]

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args (testList "client" [ tests; Fogell.Client.SourceSnapshotTests.tests ])
