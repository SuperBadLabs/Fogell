module Fogell.Client.SourceSnapshotTests

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open Expecto
open Fogell.Domain
open Fogell.Client

let private make files =
    let pipeline = Encoding.UTF8.GetBytes "pipeline { agent any; stages { stage('verify') { steps { sh 'cat input.txt' } } } }"
    let entries = files |> Array.map (fun (path, text) ->
        let bytes = Encoding.UTF8.GetBytes(text: string)
        { Path = path; ContentBase64 = Convert.ToBase64String bytes; Sha256 = SourceSnapshot.digest bytes; Executable = false })
    { SchemaVersion = 1; Kind = "fogell.source_snapshot"; PipelineBase64 = Convert.ToBase64String pipeline
      PipelineSha256 = SourceSnapshot.digest pipeline; SnapshotSha256 = SourceSnapshot.treeDigest entries
      ParentLoopId = "12345678-1234-1234-1234-123456789012"; ExpectedToolSha256 = null; Environment = SourceSnapshot.currentEnvironment (); Files = entries }
let private withRoot action =
    let root = Path.Combine(Path.GetTempPath(), "fogell-source-test-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory root |> ignore
    try action root finally Directory.Delete(root, true)
let private pack root pipeline inventory destination =
    use http = new HttpClient()
    use output = new StringWriter()
    use errors = new StringWriter()
    let args = [| "snapshot"; "--pipeline"; pipeline; "--source-root"; root; "--files-from"; inventory
                  "--output"; destination; "--parent-loop"; "12345678-1234-1234-1234-123456789012" |]
    let result = Client.run http output errors args |> fun work -> work.GetAwaiter().GetResult()
    result, output.ToString(), errors.ToString()

let tests = testList "FG-267 source snapshots" [
    testCase "snapshot hashes exact source bytes and rejects mutated payload" <| fun _ ->
        let snapshot = make [| "input.txt", "candidate-A" |]
        let bytes = SourceSnapshot.serialize snapshot
        Expect.isOk (SourceSnapshot.decode bytes) "valid envelope"
        let changed = { snapshot with Files = [| { snapshot.Files[0] with ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes "candidate-B") } |] }
        Expect.isError (SourceSnapshot.decode (SourceSnapshot.serialize changed)) "hash mismatch refused"
        let other = make [| "input.txt", "candidate-B" |]
        Expect.notEqual snapshot.SnapshotSha256 other.SnapshotSha256 "changed bytes bind different identity"
        let pipeline = { snapshot with PipelineBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes "bad") }
        Expect.isError (SourceSnapshot.decode (SourceSnapshot.serialize pipeline)) "pipeline digest checked"

    testCase "branch clean commit and credential assertions cannot enter manifest" <| fun _ ->
        let snapshot = make [| "input.txt", "dirty working-tree bytes" |]
        let json = Encoding.UTF8.GetString(SourceSnapshot.serialize snapshot)
        for injection in [ "\"revision\":\"pretend-clean-commit\","; "\"branch\":\"main\","; "\"clean\":true,"; "\"token\":\"credential-secret\","; "\"schema_version\":1," ] do
            let forged = Encoding.UTF8.GetBytes("{" + injection + json.Substring(1))
            Expect.isError (SourceSnapshot.decode forged) "unsupported/duplicate authority assertions refused"
        let identity = SourceSnapshot.identity snapshot "pending" None
        Expect.equal (identity.GetProperty("kind").GetString()) "content_snapshot" "never a verified commit"
        Expect.isFalse (identity.GetRawText().Contains "dirty working-tree") "identity does not disclose source content"

    testCase "traversal credentials collision and oversized inventory are rejected" <| fun _ ->
        for path in [ "../secret"; "/tmp/outside"; "a/../../outside"; "a\\escape"; ".git/config"; ".env"; "credentials.json"; "private.pem"; "a//b" ] do
            let snapshot = make [| path, "x" |]
            Expect.isError (SourceSnapshot.decode (SourceSnapshot.serialize snapshot)) path
        for files in [ [| "a", "x"; "a/b", "y" |]; [| "a", "x"; "A", "y" |] ] do
            Expect.isError (SourceSnapshot.decode (SourceSnapshot.serialize (make files))) "path collision"
        let tooMany = make (Array.init (SourceSnapshot.MaxFiles + 1) (fun n -> $"file{n}", ""))
        Expect.isError (SourceSnapshot.decode (SourceSnapshot.serialize tooMany)) "file count cap"

    testCase "two fresh workspaces materialize exactly identical executable input" <| fun _ ->
        withRoot <| fun root ->
            let snapshot = make [| "nested/input.txt", "same input"; "check.sh", "#!/bin/sh\ncat nested/input.txt\n" |]
            let snapshot = { snapshot with Files = snapshot.Files |> Array.map (fun f -> { f with Executable = f.Path = "check.sh" }) }
            let snapshot = { snapshot with SnapshotSha256 = SourceSnapshot.treeDigest snapshot.Files }
            for name in [ "first"; "second" ] do
                let workspace = Path.Combine(root, name)
                Directory.CreateDirectory workspace |> ignore
                Expect.isOk (SourceSnapshot.materialize snapshot workspace) "fresh workspace"
                Expect.equal (File.ReadAllText(Path.Combine(workspace, "nested/input.txt"))) "same input" "same content"
                Expect.isTrue ((File.GetUnixFileMode(Path.Combine(workspace, "check.sh")) &&& UnixFileMode.UserExecute) <> enum<UnixFileMode> 0) "executable retained"
            Expect.isError (SourceSnapshot.materialize snapshot (Path.Combine(root, "first"))) "cannot overwrite prior inputs"

    testCase "materializer refuses parent symlink and mismatched environment" <| fun _ ->
        withRoot <| fun root ->
            let snapshot = make [| "linked/input.txt", "no escape" |]
            let outside = Path.Combine(root, "outside")
            let workspace = Path.Combine(root, "workspace")
            Directory.CreateDirectory outside |> ignore
            Directory.CreateDirectory workspace |> ignore
            Directory.CreateSymbolicLink(Path.Combine(workspace, "linked"), outside) |> ignore
            Expect.isError (SourceSnapshot.materialize snapshot workspace) "no link traversal"
            Expect.isFalse (File.Exists(Path.Combine(outside, "input.txt"))) "outside unaffected"
            let wrong = { snapshot with Environment = { snapshot.Environment with Architecture = if snapshot.Environment.Architecture = "x64" then "arm64" else "x64" } }
            Expect.isError (SourceSnapshot.verifyEnvironment wrong) "controller platform must match manifest"

    testCase "pack freezes dirty source while moving Git HEAD remains unclaimed" <| fun _ ->
        withRoot <| fun root ->
            let pipeline = Path.Combine(root, "Jenkinsfile")
            let inventory = Path.Combine(root, "inventory.txt")
            let source = Path.Combine(root, "input.txt")
            File.WriteAllBytes(pipeline, Convert.FromBase64String((make [| "input.txt", "x" |]).PipelineBase64))
            File.WriteAllText(inventory, "input.txt\n")
            Directory.CreateDirectory(Path.Combine(root, ".git")) |> ignore
            File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "old-branch-or-revision")
            File.WriteAllText(source, "dirty-A")
            let first = Path.Combine(root, "first.json")
            let firstCode, firstSummary, firstError = pack root pipeline inventory first
            Expect.equal firstCode 0 firstError
            let frozen = File.ReadAllBytes first
            File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "moved-branch-or-revision")
            File.WriteAllText(source, "dirty-B")
            let secondCode, secondSummary, secondError = pack root pipeline inventory (Path.Combine(root, "second.json"))
            Expect.equal secondCode 0 secondError
            Expect.sequenceEqual (File.ReadAllBytes first) frozen "old captured input unchanged"
            Expect.notEqual firstSummary secondSummary "dirty edits change explicit content identity"
            Expect.isFalse (firstSummary.Contains("revision") || firstSummary.Contains("branch") || firstSummary.Contains("clean")) "no false Git attribution"
            Expect.equal (File.GetUnixFileMode first) (UnixFileMode.UserRead ||| UnixFileMode.UserWrite) "private envelope output"

    testCase "tool closure pins dependency content and filename deterministically" <| fun _ ->
        withRoot <| fun root ->
            let executable = Path.Combine(root, "run-host")
            File.WriteAllText(executable, "entry")
            File.WriteAllText(Path.Combine(root, "engine.dll"), "implementation-A")
            let initial = SourceSnapshot.runtimeToolDigest executable
            Expect.equal (SourceSnapshot.runtimeToolDigest executable) initial "stable fingerprint"
            File.WriteAllText(Path.Combine(root, "ignored.txt"), "not executable dependency")
            Expect.equal (SourceSnapshot.runtimeToolDigest executable) initial "only tool closure"
            File.WriteAllText(Path.Combine(root, "engine.dll"), "implementation-B")
            Expect.notEqual (SourceSnapshot.runtimeToolDigest executable) initial "dependency mutation changes identity"
            let snapshot = { make [| "input.txt", "x" |] with ExpectedToolSha256 = initial }
            Expect.isOk (SourceSnapshot.decode (SourceSnapshot.serialize snapshot)) "valid pin"
            let bad = { snapshot with ExpectedToolSha256 = "not-a-digest" }
            Expect.isError (SourceSnapshot.decode (SourceSnapshot.serialize bad)) "invalid pin refused"

    testCase "source download preserves exact admitted envelope bytes for replay" <| fun _ ->
        withRoot <| fun root ->
            let snapshot = make [| "input.txt", "reproduce" |]
            let admitted = " \n" + Encoding.UTF8.GetString(SourceSnapshot.serialize snapshot) + "\n "
            let tokenFile = Path.Combine(root, "token")
            File.WriteAllText(tokenFile, "credential")
            use handler =
                { new HttpMessageHandler() with
                    override _.SendAsync(_, _) =
                        Threading.Tasks.Task.FromResult(new HttpResponseMessage(Net.HttpStatusCode.OK, Content = new StringContent(admitted))) }
            use http = new HttpClient(handler)
            use output = new StringWriter()
            use errors = new StringWriter()
            let uuid = "12345678-1234-1234-1234-123456789012"
            let args = [| "source"; "--url"; "http://127.0.0.1"; "--organization"; uuid; "--project"; uuid; "--build"; uuid; "--token-file"; tokenFile |]
            let code = Client.run http output errors args |> fun work -> work.GetAwaiter().GetResult()
            Expect.equal code 0 (errors.ToString())
            Expect.equal (output.ToString()) admitted "no reserialization whitespace or timing decoration"

    testCase "packer excludes symlinks and credential filenames before output" <| fun _ ->
        withRoot <| fun root ->
            let pipeline = Path.Combine(root, "Jenkinsfile")
            File.WriteAllBytes(pipeline, Convert.FromBase64String((make [| "input.txt", "x" |]).PipelineBase64))
            let inventory = Path.Combine(root, "inventory.txt")
            File.WriteAllText(Path.Combine(root, "token"), "must-not-be-exported")
            File.CreateSymbolicLink(Path.Combine(root, "linked"), Path.Combine(root, "token")) |> ignore
            for path in [ "token"; "linked" ] do
                File.WriteAllText(inventory, path)
                let destination = Path.Combine(root, "refused.json")
                let code, output, _ = pack root pipeline inventory destination
                Expect.equal code 2 "input refused"
                Expect.equal output "" "no manifest leaked"
                Expect.isFalse (File.Exists destination) "no bundle produced"
]
