module Fogell.Controller.Api.ArtifactSnapshotCleanupTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fogell.Controller.Api
open Fogell.Execution

let private sha256 (value: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes value)
    |> Convert.ToHexString
    |> fun digest -> digest.ToLowerInvariant()

let private withRoot action =
    let root = Path.Combine(Path.GetTempPath(), "fogell-artifact-snapshot-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory root |> ignore

    try
        action root
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

let private layout (stateRoot: string) (org: Guid) (build: Guid) =
    let workspaceRoot = Path.Combine(stateRoot, "workspaces", org.ToString "N")
    let artifactRoot = Path.Combine(workspaceRoot, "_artifacts")
    let staging = Path.Combine(artifactRoot, build.ToString "N")
    let snapshots = Path.Combine(workspaceRoot, "_artifact-snapshots")
    workspaceRoot, artifactRoot, staging, snapshots

let private stalePending (artifactRoot: string) (build: Guid) =
    let id = sha256 (build.ToString "N")
    let pending = Path.Combine(artifactRoot, ".fogell-artifact-pending", id)
    Directory.CreateDirectory pending |> ignore
    let marker = Path.Combine(pending, "crash.part")
    File.WriteAllText(marker, "incomplete")
    pending

let artifactSnapshotCleanup =
    testList
        "artifact snapshot pending cleanup"
        [ test "finalize removes stale pending files before freezing the public snapshot" {
              withRoot (fun stateRoot ->
                  let org = Guid.NewGuid()
                  let build = Guid.NewGuid()
                  let attempt = Guid.NewGuid()
                  let _, artifactRoot, staging, snapshots = layout stateRoot org build
                  Directory.CreateDirectory staging |> ignore
                  File.WriteAllText(Path.Combine(staging, "result.bin"), "complete")
                  let pending = stalePending artifactRoot build

                  match ArtifactSnapshots.finalize stateRoot org build attempt with
                  | Error error -> failtestf "finalization refused: %s" error
                  | Ok target ->
                      Expect.isTrue (File.Exists(Path.Combine(target, "result.bin"))) "the staged result was frozen"
                      Expect.isEmpty (Directory.GetFiles(pending, "*", SearchOption.AllDirectories)) "crash bytes stay outside the public snapshot"
                      Expect.isTrue (Directory.Exists snapshots) "the attempt snapshot root exists")
          }

          test "a first-file crash is cleaned before an empty attempt snapshot is published" {
              withRoot (fun stateRoot ->
                  let org = Guid.NewGuid()
                  let build = Guid.NewGuid()
                  let attempt = Guid.NewGuid()
                  let _, artifactRoot, staging, _ = layout stateRoot org build
                  let pending = stalePending artifactRoot build
                  Expect.isFalse (Directory.Exists staging) "the first file never reached build staging"

                  match ArtifactSnapshots.finalize stateRoot org build attempt with
                  | Error error -> failtestf "first-file crash finalization refused: %s" error
                  | Ok target ->
                      Expect.isTrue (Directory.Exists target) "an empty attempt still has stable publication identity"
                      Expect.isEmpty (Directory.GetFiles(pending, "*", SearchOption.AllDirectories)) "unpublished first-file bytes are cleaned before snapshot creation")
          }

          test "a busy pending lock fails closed and leaves mutable staging intact" {
              withRoot (fun stateRoot ->
                  let org = Guid.NewGuid()
                  let build = Guid.NewGuid()
                  let attempt = Guid.NewGuid()
                  let _, artifactRoot, staging, _ = layout stateRoot org build
                  Directory.CreateDirectory staging |> ignore
                  File.WriteAllText(Path.Combine(staging, "result.bin"), "complete")
                  let pending = stalePending artifactRoot build
                  use ready = new ManualResetEventSlim(false)
                  use release = new ManualResetEventSlim(false)
                  let holder =
                      Task.Run(fun () ->
                          let lockPath =
                              Path.Combine(
                                  Path.GetFullPath artifactRoot,
                                  ".fogell-artifact-locks",
                                  sha256 (build.ToString "N") + ".lock")
                          use gate = new Mutex(false, "fogell-artifact-" + sha256 lockPath)
                          Expect.isTrue (gate.WaitOne 1000) "the fixture owns the artifact mutex"
                          ready.Set()
                          release.Wait()
                          gate.ReleaseMutex() |> ignore)

                  try
                      Expect.isTrue (ready.Wait(TimeSpan.FromSeconds 3.0)) "another thread holds the pending lock"
                      match ArtifactSnapshots.finalize stateRoot org build attempt with
                      | Ok target -> failtestf "busy cleanup unexpectedly finalized %s" target
                      | Error error -> Expect.stringContains error "busy" "the lock refusal is bounded and named"

                      Expect.isTrue (Directory.Exists staging) "mutable staging remains for recovery"
                      Expect.isTrue (File.Exists(Path.Combine(pending, "crash.part"))) "pending bytes remain recoverable"
                  finally
                      release.Set()
                      holder.Wait())
          }

          test "prepareRetry freezes parent bytes and keeps child staging isolated" {
              withRoot (fun stateRoot ->
                  let org = Guid.NewGuid()
                  let build = Guid.NewGuid()
                  let parent = Guid.NewGuid()
                  let child = Guid.NewGuid()
                  let _, _, staging, snapshots = layout stateRoot org build
                  Directory.CreateDirectory staging |> ignore
                  File.WriteAllText(Path.Combine(staging, "parent.bin"), "parent")

                  match ArtifactSnapshots.prepareRetry stateRoot org build (Some parent) with
                  | Error error -> failtestf "retry preparation refused: %s" error
                  | Ok () ->
                      let parentSnapshot = Path.Combine(snapshots, parent.ToString "N")
                      Expect.isTrue (File.Exists(Path.Combine(parentSnapshot, "parent.bin"))) "parent bytes are frozen"
                      Expect.isFalse (Directory.Exists staging) "child staging starts empty"
                      Directory.CreateDirectory staging |> ignore
                      File.WriteAllText(Path.Combine(staging, "child.bin"), "child")

                      match ArtifactSnapshots.finalize stateRoot org build child with
                      | Error error -> failtestf "child finalization refused: %s" error
                      | Ok childSnapshot ->
                          Expect.isTrue (File.Exists(Path.Combine(childSnapshot, "child.bin"))) "child bytes publish"
                          Expect.isFalse (File.Exists(Path.Combine(childSnapshot, "parent.bin"))) "parent bytes do not leak"
                          Expect.isTrue (File.Exists(Path.Combine(parentSnapshot, "parent.bin"))) "parent remains immutable")
          }

          test "concurrent finalizers retain idempotent one snapshot outcome" {
              withRoot (fun stateRoot ->
                  let org = Guid.NewGuid()
                  let build = Guid.NewGuid()
                  let attempt = Guid.NewGuid()
                  let _, _, staging, _ = layout stateRoot org build
                  Directory.CreateDirectory staging |> ignore
                  File.WriteAllText(Path.Combine(staging, "result.bin"), "complete")

                  let tasks =
                      [| 1 .. 4 |]
                      |> Array.map (fun _ ->
                          Task.Run(fun () -> ArtifactSnapshots.finalize stateRoot org build attempt))

                  Task.WaitAll(tasks |> Array.map (fun task -> task :> Task))
                  for task in tasks do
                      match task.Result with
                      | Ok _ -> ()
                      | Error error -> failtestf "concurrent finalize was not idempotent: %s" error)
          } ]
