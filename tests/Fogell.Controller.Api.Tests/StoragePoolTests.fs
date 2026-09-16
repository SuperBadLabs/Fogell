module Fogell.Controller.Api.StoragePoolTests

open System
open System.IO
open System.Runtime.InteropServices
open Expecto
open Fogell.Controller.Host

[<DllImport("libc", SetLastError = true)>]
extern int private mkfifo(string path, uint32 mode)

let private poolId = "pool-test-01"

let private policy =
    { PoolId = poolId
      MaxBytes = 1_000UL
      MaxInodes = 100UL
      MinFreeBytes = 100UL
      MinFreeInodes = 10UL }

let private stateIdentity =
    { DeviceMajor = 8u
      DeviceMinor = 1u
      MountId = 11UL }

let private poolIdentity =
    { DeviceMajor = 8u
      DeviceMinor = 2u
      MountId = 12UL }

let private healthyMetrics =
    { TotalBytes = 1_000UL
      AvailableBytes = 100UL
      TotalInodes = 100UL
      AvailableInodes = 10UL }

let private expectOk message = function
    | Ok value -> value
    | Error error -> failtestf "%s: %s" message error

let private expectErrorContaining text message = function
    | Ok _ -> failtestf "%s: unexpectedly accepted" message
    | Error error -> Expect.stringContains error text message

let private withRoot action =
    let root = Path.Combine(Path.GetTempPath(), "fogell-storage-pool-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory root |> ignore

    try
        action root
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

let private makeStateFile root (bytes: byte array) =
    let workspaces = Path.Combine(root, "workspaces")
    Directory.CreateDirectory workspaces |> ignore
    let state = Path.Combine(workspaces, ".fogell-pool-state")
    File.WriteAllBytes(state, bytes)
    File.SetUnixFileMode(state, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    state

let private makeLeaseRoot root =
    let workspaces = Path.Combine(root, "workspaces")
    Directory.CreateDirectory workspaces |> ignore
    File.WriteAllText(Path.Combine(workspaces, ".fogell-pool-id"), poolId)
    makeStateFile root (Array.zeroCreate<byte> 4096) |> ignore
    workspaces

let private dispose (lease: StoragePoolLease.PoolLease) =
    (lease :> IDisposable).Dispose()

let tests =
    testList
        "storage pool"
        [ test "finite reports on dynamic or unknown filesystems are not hard inode bounds" {
              Expect.isTrue (StoragePool.isSupportedFileSystem 0xEF53L) "ext inode tables are fixed"
              Expect.isTrue (StoragePool.isSupportedFileSystem 0x01021994L) "tmpfs nr_inodes is enforced"
              for kind in [ 0x58465342L; 0x9123683EL; 0x794C7630L; 0L ] do
                  Expect.isFalse (StoragePool.isSupportedFileSystem kind) "XFS, btrfs, overlay and unknown types fail closed"
          }
          test "an unconfigured controller cannot bypass an initialized pool" {
              withRoot (fun root ->
                  makeLeaseRoot root |> ignore
                  use admission = new StorageAdmission(root, None)
                  Expect.isFalse (admission.Ready()) "initialized pool requires its operator policy"
                  admission.CheckStart()
                  |> expectErrorContaining "policy_required" "an unconfigured worker never claims the shared pool")
          }
          testList
              "policy parsing and pure admission"
              [ test "an absent policy remains an explicit opt-out" {
                    Expect.equal (StoragePool.parse (fun _ -> null)) (Ok None) "all variables absent disable the feature"
                }

                test "a partial policy is refused rather than silently disabled" {
                    let environment name =
                        if name = "FOGELL_STORAGE_POOL_ID" then poolId else null

                    StoragePool.parse environment
                    |> expectErrorContaining "incomplete" "one configured storage setting cannot fall back to unbounded storage"
                }

                test "strict decimal policy values and minimums are validated" {
                    let values =
                        Map.ofList
                            [ "FOGELL_STORAGE_POOL_ID", poolId
                              "FOGELL_STORAGE_POOL_MAX_BYTES", "1000"
                              "FOGELL_STORAGE_POOL_MAX_INODES", "100"
                              "FOGELL_STORAGE_POOL_MIN_FREE_BYTES", "100"
                              "FOGELL_STORAGE_POOL_MIN_FREE_INODES", "10" ]
                    let environment name = values |> Map.tryFind name |> Option.toObj

                    let parsed = StoragePool.parse environment |> expectOk "valid policy parses"
                    Expect.equal parsed (Some policy) "the parsed policy preserves all declared bounds"

                    let withBadBytes = values |> Map.add "FOGELL_STORAGE_POOL_MAX_BYTES" "01x"
                    StoragePool.parse (fun name -> withBadBytes |> Map.tryFind name |> Option.toObj)
                    |> expectErrorContaining "strict positive decimal" "numeric suffixes are not accepted"

                    let withOversizedMinimum = values |> Map.add "FOGELL_STORAGE_POOL_MIN_FREE_INODES" "101"
                    StoragePool.parse (fun name -> withOversizedMinimum |> Map.tryFind name |> Option.toObj)
                    |> expectErrorContaining "must not exceed" "a guard cannot exceed the declared finite pool"
                }

                test "evaluate accepts exact free-space thresholds but rejects all capacity ambiguities" {
                    let observed =
                        StoragePool.evaluate policy stateIdentity poolIdentity healthyMetrics poolId
                        |> expectOk "exact threshold is usable"
                    Expect.equal observed.Identity poolIdentity "the admitted observation pins the mounted filesystem identity"
                    Expect.equal observed.AvailableBytes policy.MinFreeBytes "the byte threshold is inclusive"
                    Expect.equal observed.AvailableInodes policy.MinFreeInodes "the inode threshold is inclusive"

                    StoragePool.evaluate policy stateIdentity stateIdentity healthyMetrics poolId
                    |> expectErrorContaining "dedicated filesystem" "a state-root subtree is not an enforcement boundary"

                    StoragePool.evaluate policy stateIdentity poolIdentity healthyMetrics "other-pool"
                    |> expectErrorContaining "marker" "the configured pool ID is descriptor-bound admission input"

                    StoragePool.evaluate policy stateIdentity poolIdentity { healthyMetrics with AvailableBytes = 99UL } poolId
                    |> expectErrorContaining "insufficient available bytes" "byte pressure refuses admission"

                    StoragePool.evaluate policy stateIdentity poolIdentity { healthyMetrics with AvailableInodes = 9UL } poolId
                    |> expectErrorContaining "insufficient available inodes" "inode pressure refuses admission"

                    StoragePool.evaluate policy stateIdentity poolIdentity { healthyMetrics with TotalBytes = 1_001UL } poolId
                    |> expectErrorContaining "exceeds" "an unexpectedly larger filesystem is not mistaken for the configured bounded pool"

                    StoragePool.evaluate policy stateIdentity poolIdentity { healthyMetrics with AvailableBytes = 1_001UL } poolId
                    |> expectErrorContaining "exceeds its total" "nonsensical statvfs availability fails closed"
                }

                test "mount identity comparison includes mount ID" {
                    Expect.isFalse
                        (StoragePool.sameIdentity poolIdentity { poolIdentity with MountId = poolIdentity.MountId + 1UL })
                        "a replacement mount on the same device is distinct"
                } ]

          testList
              "descriptor-bound pool probe"
              [ test "a missing workspaces directory is unavailable and never created by probe" {
                    withRoot (fun root ->
                        StoragePool.probe root policy
                        |> expectErrorContaining "workspaces directory must preexist" "a lost pool mount cannot be recreated as an ordinary directory"
                        Expect.isFalse (Directory.Exists(Path.Combine(root, "workspaces"))) "the failed probe leaves no replacement pool")
                }

                test "a missing pool-ID marker is unavailable" {
                    withRoot (fun root ->
                        Directory.CreateDirectory(Path.Combine(root, "workspaces")) |> ignore
                        StoragePool.probe root policy
                        |> expectErrorContaining "marker is absent" "a directory without the operator marker has no pool identity")
                }

                test "an ordinary state-root filesystem is rejected even with well-formed markers" {
                    withRoot (fun root ->
                        makeLeaseRoot root |> ignore
                        StoragePool.probe root policy
                        |> expectErrorContaining "dedicated filesystem" "same-filesystem workspaces have no enforced pool boundary")
                }

                test "a pool-ID symlink is refused before the same-filesystem decision" {
                    withRoot (fun root ->
                        let workspaces = makeLeaseRoot root
                        let marker = Path.Combine(workspaces, ".fogell-pool-id")
                        File.Delete marker
                        let target = Path.Combine(root, "marker-target")
                        File.WriteAllText(target, poolId)
                        File.CreateSymbolicLink(marker, target) |> ignore

                        StoragePool.probe root policy
                        |> expectErrorContaining "marker" "the ID marker is opened no-follow")
                }

                test "a FIFO pool-ID marker is refused without blocking" {
                    withRoot (fun root ->
                        let workspaces = makeLeaseRoot root
                        let marker = Path.Combine(workspaces, ".fogell-pool-id")
                        File.Delete marker
                        Expect.equal (mkfifo(marker, 0x180u)) 0 "the FIFO fixture is created"

                        StoragePool.probe root policy
                        |> expectErrorContaining "regular file" "a named pipe cannot masquerade as the pool ID")
                } ]

          testList
              "exclusive dirty lease"
              [ test "a normal active-to-terminal lifecycle durably returns the marker to all zeroes" {
                    withRoot (fun root ->
                        let workspaces = makeLeaseRoot root
                        let state = Path.Combine(workspaces, ".fogell-pool-state")
                        let lease = StoragePoolLease.tryOpen root |> expectOk "precreated idle marker opens"

                        try
                            lease.CheckIdle() |> expectOk "fresh marker is idle" |> ignore
                            lease.MarkActive "org/attempt/7" |> expectOk "activation is durable before launch" |> ignore
                            lease.CheckIdle()
                            |> expectErrorContaining "dirty" "a live writer cannot be admitted as idle"
                            lease.Complete() |> expectOk "only a successful terminal path clears the marker" |> ignore
                            lease.CheckIdle() |> expectOk "completion returns the held marker to idle" |> ignore
                        finally
                            dispose lease
                        Expect.isTrue (File.ReadAllBytes(state) |> Array.forall ((=) 0uy)) "completion persists the full zero buffer")
                }

                test "a dirty marker survives lease close and blocks a later controller" {
                    withRoot (fun root ->
                        makeLeaseRoot root |> ignore
                        let first = StoragePoolLease.tryOpen root |> expectOk "first controller owns marker"
                        first.MarkActive "org/attempt/8" |> expectOk "first controller records dirty state" |> ignore
                        dispose first

                        let restarted = StoragePoolLease.tryOpen root |> expectOk "a restart may acquire the released flock"
                        try
                            restarted.CheckIdle()
                            |> expectErrorContaining "manual reconciliation" "restart cannot reinterpret prior writer state"
                        finally
                            dispose restarted)
                }

                test "a second controller cannot obtain the exclusive flock" {
                    withRoot (fun root ->
                        makeLeaseRoot root |> ignore
                        let first = StoragePoolLease.tryOpen root |> expectOk "first controller owns the flock"
                        try
                            StoragePoolLease.tryOpen root
                            |> expectErrorContaining "already leased" "capacity one is held across independent opens"
                        finally
                            dispose first)
                }

                test "short, nonzero, symlink, and FIFO state markers are all refused" {
                    withRoot (fun root ->
                        let shortRoot = Path.Combine(root, "short")
                        Directory.CreateDirectory shortRoot |> ignore
                        makeStateFile shortRoot (Array.zeroCreate<byte> 4095) |> ignore
                        StoragePoolLease.tryOpen shortRoot
                        |> expectErrorContaining "exactly 4096" "a truncated marker cannot be treated as clean"

                        let dirtyRoot = Path.Combine(root, "dirty")
                        Directory.CreateDirectory dirtyRoot |> ignore
                        let dirty = Array.zeroCreate<byte> 4096
                        dirty[2048] <- 1uy
                        makeStateFile dirtyRoot dirty |> ignore
                        let dirtyLease = StoragePoolLease.tryOpen dirtyRoot |> expectOk "a full-size dirty marker still opens for inspection"
                        try
                            dirtyLease.CheckIdle()
                            |> expectErrorContaining "manual reconciliation" "any nonzero byte in a full-size marker blocks restart"
                        finally
                            dispose dirtyLease

                        let fifoRoot = Path.Combine(root, "fifo")
                        Directory.CreateDirectory fifoRoot |> ignore
                        let workspaces = Path.Combine(fifoRoot, "workspaces")
                        Directory.CreateDirectory workspaces |> ignore
                        let fifo = Path.Combine(workspaces, ".fogell-pool-state")
                        Expect.equal (mkfifo(fifo, 0x180u)) 0 "the state FIFO fixture is created"
                        StoragePoolLease.tryOpen fifoRoot
                        |> expectErrorContaining "regular" "a FIFO state marker is not opened as a lease"

                        let linkRoot = Path.Combine(root, "link")
                        Directory.CreateDirectory linkRoot |> ignore
                        let linkWorkspaces = Path.Combine(linkRoot, "workspaces")
                        Directory.CreateDirectory linkWorkspaces |> ignore
                        let target = Path.Combine(root, "state-target")
                        File.WriteAllBytes(target, Array.zeroCreate<byte> 4096)
                        File.SetUnixFileMode(target, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                        File.CreateSymbolicLink(Path.Combine(linkWorkspaces, ".fogell-pool-state"), target) |> ignore
                        StoragePoolLease.tryOpen linkRoot
                        |> expectErrorContaining "nofollow" "the state marker cannot be redirected through a link")
                }

                test "path replacement while leased is detected before any clear" {
                    withRoot (fun root ->
                        let workspaces = makeLeaseRoot root
                        let state = Path.Combine(workspaces, ".fogell-pool-state")
                        let replacement = state + ".old"
                        let lease = StoragePoolLease.tryOpen root |> expectOk "lease opens the original inode"

                        try
                            File.Move(state, replacement)
                            File.WriteAllBytes(state, Array.zeroCreate<byte> 4096)
                            File.SetUnixFileMode(state, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                            StoragePoolLease.tryOpen root
                            |> expectErrorContaining "already leased" "replacing the state file cannot split the mounted-directory lock"
                            lease.CheckIdle()
                            |> expectErrorContaining "replaced" "a path substitution cannot validate the leased descriptor"
                            lease.Complete()
                            |> expectErrorContaining "replaced" "a replacement marker is never cleared through the old lease"
                            Expect.isTrue (File.ReadAllBytes(state) |> Array.forall ((=) 0uy)) "the replacement file remains untouched"
                        finally
                            dispose lease)
                } ] ]
