module Fogell.Execution.ArtifactPublishingTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fogell.Execution

[<DllImport("libc", SetLastError = true)>]
extern int private mkfifo(string path, uint32 mode)

[<DllImport("libc", SetLastError = true)>]
extern int private link(string existingPath, string newPath)

let private root () =
    let value = Path.Combine(Path.GetTempPath(), "fogell-artifacts-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory value |> ignore
    value

let private limits fileBytes totalBytes files scanEntries : ArtifactLimits =
    { MaxFileBytes = fileBytes
      MaxTotalBytes = totalBytes
      MaxFiles = files
      MaxScanEntries = scanEntries }

let private writeBytes (path: string) (count: int) (value: byte) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllBytes(path, Array.create count value)

let private archive store workspace patterns abort =
    Publish.archiveWithAbort store "build-1" workspace patterns abort

let private buildTarget root = Path.Combine(root, "build-1")

let private pendingFiles (root: string) : string array =
    let sidecar = Path.Combine(root, ".fogell-artifact-pending")

    if Directory.Exists sidecar then
        Directory.GetFiles(sidecar, "*.part", SearchOption.AllDirectories)
    else
        [||]

let private expectLimit reason work =
    try
        work () |> ignore
        failtestf "expected artifact limit %A" reason
    with :? ArtifactLimitExceededException as ex ->
        Expect.equal ex.Reason reason "the public reason identifies the exhausted limit"

let private expectIo work =
    Expect.throwsT<IOException> work "unsafe archive input is refused as an I/O failure"

let private withRoot action =
    let value = root ()

    try
        action value
    finally
        if Directory.Exists value then
            Directory.Delete(value, true)

let private sha256 (text: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes text)
    |> Convert.ToHexString
    |> fun value -> value.ToLowerInvariant()

let private artifactMutexName root =
    let buildId = sha256 "build-1"
    let lockPath = Path.Combine(Path.GetFullPath root, ".fogell-artifact-locks", buildId + ".lock")
    "fogell-artifact-" + sha256 lockPath

let artifactPublishing =
    let tests =
        [ test "file and total-byte boundaries admit exact values and reject one extra byte" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let store = ArtifactStore.underWithLimits value (limits 8L 12L 10 100)
                  writeBytes (Path.Combine(workspace, "eight.bin")) 8 1uy
                  writeBytes (Path.Combine(workspace, "nine.bin")) 9 2uy
                  writeBytes (Path.Combine(workspace, "four.bin")) 4 3uy
                  writeBytes (Path.Combine(workspace, "one.bin")) 1 4uy

                  Expect.sequenceEqual
                      (archive store workspace [ "eight.bin" ] (fun () -> false) |> fst)
                      [ "eight.bin" ]
                      "a file exactly at its limit is published"

                  expectLimit ArtifactLimitReason.FileBytes (fun () ->
                      archive store workspace [ "nine.bin" ] (fun () -> false))

                  Expect.sequenceEqual
                      (archive store workspace [ "four.bin" ] (fun () -> false) |> fst)
                      [ "four.bin" ]
                      "the exact retained total is published across calls"

                  expectLimit ArtifactLimitReason.TotalBytes (fun () ->
                      archive store workspace [ "one.bin" ] (fun () -> false))

                  Expect.equal (FileInfo(Path.Combine(buildTarget value, "eight.bin")).Length) 8L "first bytes remain"
                  Expect.equal (FileInfo(Path.Combine(buildTarget value, "four.bin")).Length) 4L "exact-total bytes remain"
                  Expect.isFalse (File.Exists(Path.Combine(buildTarget value, "one.bin"))) "over-budget bytes are absent"

                  writeBytes (Path.Combine(workspace, "eight.bin")) 8 8uy
                  let replacementStore = ArtifactStore.underWithLimits value (limits 8L 20L 10 100)
                  Expect.sequenceEqual
                      (archive replacementStore workspace [ "eight.bin" ] (fun () -> false) |> fst)
                      [ "eight.bin" ]
                      "a replacement exactly at retained-plus-sidecar capacity is admitted"
                  Expect.sequenceEqual
                      (File.ReadAllBytes(Path.Combine(buildTarget value, "eight.bin")))
                      (Array.create 8 8uy)
                      "a successful replacement commits the new complete file")
          }

          test "file count is exact and is recomputed across stores and calls" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let cap = limits 100L 100L 2 100
                  let first = ArtifactStore.underWithLimits value cap
                  let second = ArtifactStore.underWithLimits value cap
                  writeBytes (Path.Combine(workspace, "one")) 1 1uy
                  writeBytes (Path.Combine(workspace, "two")) 1 2uy
                  writeBytes (Path.Combine(workspace, "three")) 1 3uy

                  archive first workspace [ "one" ] (fun () -> false) |> ignore
                  archive second workspace [ "two" ] (fun () -> false) |> ignore
                  expectLimit ArtifactLimitReason.FileCount (fun () ->
                      archive first workspace [ "three" ] (fun () -> false))

                  Expect.sequenceEqual
                      (Directory.GetFiles(buildTarget value) |> Array.map Path.GetFileName |> Array.sort)
                      [| "one"; "two" |]
                      "the active staging directory itself, not a per-object ledger, supplies the count")
          }

          test "a same-attempt restart rescans the active staging filesystem" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let policy = limits 100L 8L 10 100
                  let firstProcess = ArtifactStore.underWithLimits value policy
                  writeBytes (Path.Combine(workspace, "first")) 3 1uy
                  archive firstProcess workspace [ "first" ] (fun () -> false) |> ignore

                  // Simulate bytes retained while Run.Host was down. A fresh
                  // store must not rely on an in-memory cumulative-copy ledger.
                  writeBytes (Path.Combine(buildTarget value, "retained-on-restart")) 5 2uy
                  writeBytes (Path.Combine(workspace, "after-restart")) 1 3uy
                  let restartedProcess = ArtifactStore.underWithLimits value policy
                  expectLimit ArtifactLimitReason.TotalBytes (fun () ->
                      archive restartedProcess workspace [ "after-restart" ] (fun () -> false))
                  Expect.isFalse (File.Exists(Path.Combine(buildTarget value, "after-restart"))) "the post-restart overage is not published")
          }

          test "the total ceiling charges every chunk of one active sidecar" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let store = ArtifactStore.underWithLimits value (limits 100_000L 100_000L 10 100)
                  writeBytes (Path.Combine(workspace, "retained")) 30_000 1uy
                  writeBytes (Path.Combine(workspace, "two-chunks")) 80_000 2uy
                  archive store workspace [ "retained" ] (fun () -> false) |> ignore
                  expectLimit ArtifactLimitReason.TotalBytes (fun () ->
                      archive store workspace [ "two-chunks" ] (fun () -> false))
                  Expect.isFalse (File.Exists(Path.Combine(buildTarget value, "two-chunks"))) "later chunks cannot make the final retained set exceed its total ceiling"
                  Expect.isEmpty (pendingFiles value) "a later-chunk quota failure deletes the private sidecar")
          }

          test "scan-entry ceiling is enforced while exact ceiling remains usable" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let store = ArtifactStore.underWithLimits value (limits 100L 100L 10 2)
                  writeBytes (Path.Combine(workspace, "one")) 1 1uy
                  writeBytes (Path.Combine(workspace, "two")) 1 2uy

                  Expect.sequenceEqual
                      (archive store workspace [ "*" ] (fun () -> false) |> fst)
                      [ "one"; "two" ]
                      "the scan ceiling is inclusive"

                  writeBytes (Path.Combine(workspace, "three")) 1 3uy
                  expectLimit ArtifactLimitReason.ScanEntries (fun () ->
                      archive store workspace [ "*" ] (fun () -> false)))
          }

          test "parallel writers share the retained total rather than each admitting a local budget" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let cap = limits 10L 10L 10 100
                  writeBytes (Path.Combine(workspace, "left")) 6 1uy
                  writeBytes (Path.Combine(workspace, "right")) 6 2uy
                  use start = new ManualResetEventSlim(false)
                  use ready = new CountdownEvent(2)

                  let publish name =
                          Task.Run(fun () ->
                          ready.Signal() |> ignore
                          start.Wait()

                          try
                              archive (ArtifactStore.underWithLimits value cap) workspace [ name ] (fun () -> false)
                              |> ignore
                              Ok()
                          with :? ArtifactLimitExceededException as ex ->
                              Error ex.Reason)

                  let left = publish "left"
                  let right = publish "right"
                  Expect.isTrue (ready.Wait(TimeSpan.FromSeconds 3.0)) "both writers reached the start gate"
                  start.Set()
                  Task.WaitAll [| left :> Task; right :> Task |]
                  let results = [ left.Result; right.Result ]
                  Expect.equal (results |> List.filter Result.isOk |> List.length) 1 "exactly one six-byte writer commits"
                  Expect.equal
                      (results |> List.filter (function | Error ArtifactLimitReason.TotalBytes -> true | _ -> false) |> List.length)
                      1
                      "the other concurrent writer observes the shared total"
                  Expect.equal
                      (Directory.GetFiles(buildTarget value) |> Array.sumBy (fun path -> FileInfo(path).Length))
                      6L
                      "the stored total never exceeds the cap")
          }

          test "a source that grows after copy starts is bounded by bytes actually read" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let source = Path.Combine(workspace, "growing.bin")
                  let store = ArtifactStore.underWithLimits value (limits (40L * 1024L * 1024L) (40L * 1024L * 1024L) 10 100)
                  writeBytes source (32 * 1024 * 1024) 1uy
                  let mutable appended = false

                  let appendAfterFirstChunk () =
                      if not appended && (pendingFiles value |> Array.exists (fun path -> FileInfo(path).Length > 0L)) then
                          use output = new FileStream(source, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
                          let appendedBytes: byte array = Array.create (16 * 1024 * 1024) 2uy
                          output.Write appendedBytes
                          output.Flush(true)
                          appended <- true

                      false

                  expectLimit ArtifactLimitReason.FileBytes (fun () ->
                      archive store workspace [ "growing.bin" ] appendAfterFirstChunk)
                  Expect.isTrue appended "the source grows only after the first bounded chunk is staged"
                  Expect.isFalse (File.Exists(Path.Combine(buildTarget value, "growing.bin"))) "a growing source never publishes a truncated final file"
                  Expect.isEmpty (pendingFiles value) "the rejected active copy leaves no sidecar payload")
          }

          test "mid-file and final-chunk cancellation remove the sidecar and preserve the previous file" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let source = Path.Combine(workspace, "same.bin")
                  let store = ArtifactStore.underWithLimits value (limits (2L * 1024L * 1024L) (2L * 1024L * 1024L) 10 100)
                  writeBytes source 4 1uy
                  archive store workspace [ "same.bin" ] (fun () -> false) |> ignore
                  writeBytes source (1024 * 1024) 9uy

                  let cancelMidCopy () =
                      pendingFiles value
                      |> Array.exists (fun path -> FileInfo(path).Length > 0L)

                  let _, midAborted = archive store workspace [ "same.bin" ] cancelMidCopy
                  Expect.isTrue midAborted "cancellation during a chunk is reported"
                  Expect.sequenceEqual (File.ReadAllBytes(Path.Combine(buildTarget value, "same.bin"))) (Array.create 4 1uy) "the old committed file survives a cancelled replacement"
                  Expect.isEmpty (pendingFiles value) "mid-file cancellation deletes the active sidecar"

                  writeBytes source 32 7uy

                  let cancelAfterFinalChunk () =
                      pendingFiles value
                      |> Array.exists (fun path -> FileInfo(path).Length = 32L)

                  let _, finalChunkAborted = archive store workspace [ "same.bin" ] cancelAfterFinalChunk
                  Expect.isTrue finalChunkAborted "the poll after the final chunk remains authoritative"
                  Expect.sequenceEqual (File.ReadAllBytes(Path.Combine(buildTarget value, "same.bin"))) (Array.create 4 1uy) "post-copy cancellation cannot replace the good file"
                  Expect.isEmpty (pendingFiles value) "post-copy cancellation deletes the sidecar")
          }

          test "source symlinks, FIFOs, and matching linked directories cannot publish outside bytes" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let outside = Path.Combine(value, "outside")
                  let store = ArtifactStore.underWithLimits value (limits 100L 100L 10 100)
                  let outsideFile = Path.Combine(outside, "secret")
                  writeBytes outsideFile 5 7uy
                  Directory.CreateDirectory workspace |> ignore
                  File.CreateSymbolicLink(Path.Combine(workspace, "file-link"), outsideFile) |> ignore
                  expectIo (fun () -> archive store workspace [ "file-link" ] (fun () -> false) |> ignore)

                  let fifo = Path.Combine(workspace, "blocked-fifo")
                  Expect.equal (mkfifo(fifo, 0o600u)) 0 "FIFO fixture exists"
                  let fifoAttempt = Task.Run(fun () -> expectIo (fun () -> archive store workspace [ "blocked-fifo" ] (fun () -> false) |> ignore))
                  Expect.isTrue (fifoAttempt.Wait(TimeSpan.FromSeconds 3.0)) "nonblocking source validation refuses FIFO promptly"

                  Directory.CreateSymbolicLink(Path.Combine(workspace, "dir-link"), outside) |> ignore
                  let copied, aborted = archive store workspace [ "dir-link/**" ] (fun () -> false)
                  Expect.isFalse aborted "an ignored linked directory does not masquerade as cancellation"
                  Expect.isEmpty copied "a matching directory link is never traversed"
                  Expect.isFalse (File.Exists(Path.Combine(buildTarget value, "dir-link", "secret"))) "outside content is not archived")
          }

          test "hostile destination symlinks are refused and hard-link replacement is atomic" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let store = ArtifactStore.underWithLimits value (limits 100L 100L 10 100)
                  let destination = Path.Combine(buildTarget value, "result")
                  let symlinkCanary = Path.Combine(value, "symlink-canary")
                  let hardlinkCanary = Path.Combine(value, "hardlink-canary")
                  Directory.CreateDirectory(buildTarget value) |> ignore
                  writeBytes symlinkCanary 4 1uy
                  File.CreateSymbolicLink(destination, symlinkCanary) |> ignore
                  writeBytes (Path.Combine(workspace, "result")) 4 2uy
                  expectIo (fun () -> archive store workspace [ "result" ] (fun () -> false) |> ignore)
                  Expect.sequenceEqual (File.ReadAllBytes symlinkCanary) (Array.create 4 1uy) "refusing a destination link never writes its target"
                  Expect.isTrue (File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint)) "the unsafe destination entry is left in place"

                  writeBytes hardlinkCanary 4 3uy
                  File.Delete destination
                  Expect.equal (link(hardlinkCanary, destination)) 0 "hard-link fixture exists"
                  writeBytes (Path.Combine(workspace, "result")) 4 4uy
                  archive store workspace [ "result" ] (fun () -> false) |> ignore
                  Expect.sequenceEqual (File.ReadAllBytes hardlinkCanary) (Array.create 4 3uy) "atomic replacement does not mutate the external hard-link inode"
                  Expect.sequenceEqual (File.ReadAllBytes destination) (Array.create 4 4uy) "the new artifact has its own bytes"

                  let directoryCanary = Path.Combine(value, "directory-canary")
                  Directory.CreateDirectory directoryCanary |> ignore
                  Directory.CreateSymbolicLink(Path.Combine(buildTarget value, "nested"), directoryCanary) |> ignore
                  writeBytes (Path.Combine(workspace, "nested", "result")) 4 5uy
                  expectIo (fun () -> archive store workspace [ "nested/result" ] (fun () -> false) |> ignore)
                  Expect.isFalse (File.Exists(Path.Combine(directoryCanary, "result"))) "a linked destination ancestor cannot redirect atomic publication")
          }

          test "restart cleanup removes stale sidecars and a cancelled lock wait makes no artifact" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let store = ArtifactStore.underWithLimits value (limits 100L 100L 10 100)
                  writeBytes (Path.Combine(workspace, "result")) 4 5uy
                  archive store workspace [ "result" ] (fun () -> false) |> ignore
                  let pendingRoot = Path.Combine(value, ".fogell-artifact-pending")
                  let pending = Directory.GetDirectories(pendingRoot) |> Array.exactlyOne
                  let stale = Path.Combine(pending, "interrupted.part")
                  File.WriteAllText(stale, "partial")
                  archive store workspace [ "result" ] (fun () -> false) |> ignore
                  Expect.isFalse (File.Exists stale) "a restarted archive clears stale private data before publishing"

                  use ready = new ManualResetEventSlim(false)
                  use release = new ManualResetEventSlim(false)
                  let holder =
                      Task.Run(fun () ->
                          use held = new Mutex(false, artifactMutexName value)
                          Expect.isTrue (held.WaitOne(TimeSpan.FromSeconds 3.0)) "the fixture owns the archive mutex"
                          ready.Set()
                          release.Wait()
                          held.ReleaseMutex())

                  try
                      Expect.isTrue (ready.Wait(TimeSpan.FromSeconds 3.0)) "another thread holds the build lock"
                      let mutable polls = 0
                      let _, aborted =
                          archive store workspace [ "result" ] (fun () ->
                              polls <- polls + 1
                              polls >= 2)
                      Expect.isTrue aborted "waiting for another writer remains cancellable"
                      Expect.isTrue (polls >= 2) "the lock wait repeatedly polls the cancellation source"
                  finally
                      release.Set()
                      holder.Wait())
          }

          test "a failed lock acquisition releases its named mutex before another archive waits" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let store = ArtifactStore.underWithLimits value (limits 100L 100L 10 100)
                  writeBytes (Path.Combine(workspace, "result")) 4 6uy
                  let buildId = sha256 "build-1"
                  let lockRelative = Path.Combine(".fogell-artifact-locks", buildId + ".lock")
                  let lockPath = Path.Combine(Path.GetFullPath value, lockRelative)

                  // Keep another handle open so disposing the failing caller's
                  // Mutex does not mask a missing ReleaseMutex.
                  use survivingHandle = new Mutex(false, artifactMutexName value)
                  use releaseFailingThread = new ManualResetEventSlim(false)
                  let observedFailure = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                  let failingAcquisition =
                      Task.Run(fun () ->
                          try
                              Publish.acquireArtifactLockUsing
                                  (fun _ -> raise (IOException "injected lock failure"))
                                  value
                                  lockRelative
                                  lockPath
                                  (fun () -> false)
                                  false
                              |> ignore
                              failtest "the injected lock callback unexpectedly returned"
                          with error ->
                              observedFailure.SetException error |> ignore
                              // Keep this OS thread alive after it has reported
                              // the exception. The old implementation retained
                              // mutex ownership on this thread, so a different
                              // archive task would hit its bounded abort below.
                              releaseFailingThread.Wait()
                              Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw())

                  try
                      Expect.throwsT<IOException>
                          (fun () -> observedFailure.Task.GetAwaiter().GetResult())
                          "the lock acquisition failure reaches its caller"

                      let deadline = System.Diagnostics.Stopwatch.StartNew()
                      let recovery =
                          Task.Run(fun () ->
                              archive store workspace [ "result" ] (fun () ->
                                  deadline.Elapsed >= TimeSpan.FromSeconds 2.0))

                      Expect.isTrue (recovery.Wait(TimeSpan.FromSeconds 3.0)) "a later writer cannot remain blocked by a failed owner"
                      let copied, aborted = recovery.Result
                      Expect.isFalse aborted "the later archive acquires the released mutex before its bounded abort"
                      Expect.sequenceEqual copied [ "result" ] "the recovered writer publishes normally"
                  finally
                      releaseFailingThread.Set()

                  Expect.throwsT<IOException>
                      (fun () -> failingAcquisition.GetAwaiter().GetResult())
                      "the injected task itself remains faulted after releasing its test thread")
          }

          test "reserved build keys and a linked private sidecar cannot redirect cleanup" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let store = ArtifactStore.underWithLimits value (limits 100L 100L 10 100)
                  writeBytes (Path.Combine(workspace, "payload")) 4 9uy

                  for reserved in [ ".fogell-artifact-pending"; ".fogell-artifact-locks" ] do
                      expectIo (fun () ->
                          Publish.archiveWithAbort store reserved workspace [ "payload" ] (fun () -> false)
                          |> ignore)

                  let outside = Path.Combine(value, "outside")
                  let buildId = sha256 "build-1"
                  let externalPending = Path.Combine(outside, buildId)
                  Directory.CreateDirectory externalPending |> ignore
                  let canary = Path.Combine(externalPending, "canary.part")
                  File.WriteAllText(canary, "outside data")
                  Directory.CreateSymbolicLink(Path.Combine(value, ".fogell-artifact-pending"), outside) |> ignore

                  Expect.isFalse
                      (Publish.cleanupPending store "build-1" (fun () -> false))
                      "linked sidecar storage is refused rather than treated as private stale state"
                  Expect.isTrue (File.Exists canary) "cleanup never deletes a file through a linked sidecar root")
          }

          test "a FIFO substituted for the sidecar lock fails promptly without publishing" {
              withRoot (fun value ->
                  let workspace = Path.Combine(value, "workspace")
                  let store = ArtifactStore.underWithLimits value (limits 100L 100L 10 100)
                  writeBytes (Path.Combine(workspace, "payload")) 4 1uy
                  let lockRoot = Path.Combine(value, ".fogell-artifact-locks")
                  Directory.CreateDirectory lockRoot |> ignore
                  let lockPath = Path.Combine(lockRoot, sha256 "build-1" + ".lock")
                  Expect.equal (mkfifo(lockPath, 0o600u)) 0 "the hostile lock entry is a FIFO"

                  let attempt =
                      Task.Run(fun () ->
                          expectIo (fun () -> archive store workspace [ "payload" ] (fun () -> false) |> ignore))

                  Expect.isTrue (attempt.Wait(TimeSpan.FromSeconds 3.0)) "a FIFO lock cannot block artifact publication"
                  Expect.isFalse (File.Exists(Path.Combine(buildTarget value, "payload"))) "an unsafe lock never admits an artifact")
          } ]

    if OperatingSystem.IsLinux() then
        testList "artifact publishing bounds and custody" tests
    else
        ptestList "artifact publishing bounds and custody" tests
