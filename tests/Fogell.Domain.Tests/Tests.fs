module Fogell.Domain.Tests

open System
open Expecto
open System.Runtime.InteropServices
open FsCheck
open Fogell.Domain

let private allStatuses = [ NotBuilt; Success; Unstable; Failure; Aborted ]

let private statusGen = Gen.elements allStatuses |> Arb.fromGen

/// FsCheck generator registration for BuildStatus.
type StatusArb() =
    static member BuildStatus() = statusGen

/// FG-006 acceptance: worstOf is associative and commutative, with NotBuilt as
/// identity. That is what makes parallel stage aggregation order-independent.
let aggregationProperties =
    testList
        "BuildStatus.worstOf forms a commutative monoid"
        [ test "commutative (exhaustive)" {
              for a in allStatuses do
                  for b in allStatuses do
                      Expect.equal
                          (BuildStatus.worstOf a b)
                          (BuildStatus.worstOf b a)
                          $"worstOf %A{a} %A{b} must commute"
          }

          test "associative (exhaustive)" {
              for a in allStatuses do
                  for b in allStatuses do
                      for c in allStatuses do
                          Expect.equal
                              (BuildStatus.worstOf (BuildStatus.worstOf a b) c)
                              (BuildStatus.worstOf a (BuildStatus.worstOf b c))
                              $"worstOf must associate over %A{(a, b, c)}"
          }

          test "NotBuilt is the identity" {
              for a in allStatuses do
                  Expect.equal (BuildStatus.worstOf NotBuilt a) a "left identity"
                  Expect.equal (BuildStatus.worstOf a NotBuilt) a "right identity"
          }

          testPropertyWithConfig
              { FsCheckConfig.defaultConfig with arbitrary = [ typeof<StatusArb> ] }
              "ofMany is order-independent"
              (fun (statuses: BuildStatus list) ->
                  let forward = BuildStatus.ofMany statuses
                  let reversed = BuildStatus.ofMany (List.rev statuses)
                  forward = reversed)

          test "an abort is not masked by a later failure" {
              Expect.equal (BuildStatus.ofMany [ Aborted; Failure ]) Aborted "operator intent dominates"
          }

          test "wire round-trip is total over the type" {
              for a in allStatuses do
                  Expect.equal (BuildStatus.ofWireString (BuildStatus.toWireString a)) (Some a) "round trip"
          } ]

let transitionProperties =
    testList
        "AttemptState transitions"
        [ test "a terminal attempt never transitions again" {
              for s in allStatuses do
                  for target in
                      [ Queued; Offered; Accepted; Running; Finalizing; Cancelling; ReconciliationRequired ] do
                      Expect.isFalse
                          (AttemptState.canTransition (Terminal s) target)
                          $"Terminal %A{s} -> %A{target} must be illegal"
          }

          test "every active state may enter reconciliation" {
              for s in [ Queued; Offered; Accepted; Running; Finalizing; Cancelling ] do
                  Expect.isTrue
                      (AttemptState.canTransition s ReconciliationRequired)
                      $"%A{s} -> ReconciliationRequired"
          }

          test "an expired offer returns to the queue" {
              Expect.isTrue (AttemptState.canTransition Offered Queued) "offer expiry requeues"
          }

          test "queued work cannot jump straight to running" {
              Expect.isFalse (AttemptState.canTransition Queued Running) "must be offered and accepted first"
          } ]

let private attempt state fence epoch owner expiry =
    { Id = AttemptId(Guid.NewGuid())
      NodeId = NodeId(Guid.NewGuid())
      OrganizationId = OrganizationId(Guid.NewGuid())
      Ordinal = 0
      RetryOf = None
      State = state
      Fence = fence
      RestoreEpoch = epoch
      LeaseOwner = owner
      LeaseExpiresAt = expiry }

let publicationGuard =
    let now = DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero)
    let future = Some(now.AddMinutes 5.0)
    let epoch = RestoreEpoch 3L
    let fence = Fence 7L
    let good = attempt Running fence epoch (Some "agent-a") future

    testList
        "terminal publication guard"
        [ test "the exact current holder may publish" {
              Expect.isTrue (Attempt.mayPublish now epoch fence "agent-a" good) "happy path"
          }
          test "a stale fence may not" {
              Expect.isFalse (Attempt.mayPublish now epoch (Fence 8L) "agent-a" good) "fence mismatch"
          }
          test "a pre-restore epoch may not" {
              Expect.isFalse
                  (Attempt.mayPublish now (RestoreEpoch 4L) fence "agent-a" good)
                  "restore invalidates prior leases"
          }
          test "another owner may not" {
              Expect.isFalse (Attempt.mayPublish now epoch fence "agent-b" good) "wrong lease owner"
          }
          test "an expired lease may not" {
              let expired = { good with LeaseExpiresAt = Some(now.AddSeconds -1.0) }
              Expect.isFalse (Attempt.mayPublish now epoch fence "agent-a" expired) "lease expiry is enforced"
          }
          test "a leaseless attempt may not" {
              Expect.isFalse
                  (Attempt.mayPublish now epoch fence "agent-a" { good with LeaseOwner = None })
                  "no lease, no publication"
          }
          test "an already-terminal attempt may not" {
              Expect.isFalse
                  (Attempt.mayPublish now epoch fence "agent-a" { good with State = Terminal Success })
                  "terminal is final"
          } ]

let retrySemantics =
    testList
        "retry never rewrites history"
        [ test "a child links to its parent and resets the lease" {
              let parent =
                  attempt (Terminal Failure) (Fence 4L) (RestoreEpoch 1L) (Some "agent-a") None

              let childId = AttemptId(Guid.NewGuid())
              let child = Attempt.retryOf childId parent
              Expect.equal child.Ordinal (parent.Ordinal + 1) "ordinal increments"
              Expect.equal child.RetryOf (Some parent.Id) "immutable link to parent"
              Expect.equal child.State Queued "child starts queued"
              Expect.equal child.Fence Fence.initial "fence resets"
              Expect.isNone child.LeaseOwner "no inherited lease"
              Expect.notEqual child.Id parent.Id "parent identity is preserved"
          } ]

/// FG-238. O_DIRECTORY and O_NOFOLLOW are per-architecture kernel ABI. The
/// values here are read from the kernel's own headers
/// (`include/uapi/asm-generic/fcntl.h`, `arch/arm64/include/uapi/asm/fcntl.h`),
/// not from a running process, so the test pins the table to the ABI rather
/// than to whatever machine happens to run it.
let linuxOpenFlags =
    testList
        "FG-238 open(2) flag bits follow the kernel's per-architecture ABI"
        [ test "the asm-generic table is O_DIRECTORY=0o200000, O_NOFOLLOW=0o400000" {
              Expect.equal LinuxOpenFlags.asmGeneric.Directory 0x10000 "O_DIRECTORY (asm-generic)"
              Expect.equal LinuxOpenFlags.asmGeneric.NoFollow 0x20000 "O_NOFOLLOW (asm-generic)"
          }

          test "the arm lineage table is O_DIRECTORY=0o40000, O_NOFOLLOW=0o100000" {
              Expect.equal LinuxOpenFlags.armLineage.Directory 0x4000 "O_DIRECTORY (arm64)"
              Expect.equal LinuxOpenFlags.armLineage.NoFollow 0x8000 "O_NOFOLLOW (arm64)"
          }

          test "the asm-generic bits are O_DIRECT|O_LARGEFILE on arm64, which is why guessing fails" {
              // arm64: O_DIRECT = 0o200000, O_LARGEFILE = 0o400000 — the exact
              // bits the generic table calls O_DIRECTORY and O_NOFOLLOW.
              Expect.notEqual LinuxOpenFlags.asmGeneric LinuxOpenFlags.armLineage "the two tables differ"
              Expect.equal
                  (LinuxOpenFlags.asmGeneric.Directory ||| LinuxOpenFlags.asmGeneric.NoFollow)
                  (0o200000 ||| 0o400000)
                  "on arm64 those bits are O_DIRECT|O_LARGEFILE"
          }

          test "x86, x86-64, riscv64, s390x and loongarch64 use the asm-generic table" {
              for architecture in
                  [ Architecture.X86
                    Architecture.X64
                    Architecture.RiscV64
                    Architecture.S390x
                    Architecture.LoongArch64 ] do
                  Expect.equal
                      (LinuxOpenFlags.forArchitecture architecture)
                      (Ok LinuxOpenFlags.asmGeneric)
                      $"%A{architecture} is asm-generic"
          }

          test "arm, armv6, arm64 and ppc64le use the arm lineage table" {
              for architecture in
                  [ Architecture.Arm; Architecture.Armv6; Architecture.Arm64; Architecture.Ppc64le ] do
                  Expect.equal
                      (LinuxOpenFlags.forArchitecture architecture)
                      (Ok LinuxOpenFlags.armLineage)
                      $"%A{architecture} is arm lineage"
          }

          test "an untabulated architecture is a refusal, never a guess" {
              match LinuxOpenFlags.forArchitecture Architecture.Wasm with
              | Ok table -> failtestf "Wasm received a table: %A" table
              | Error why -> Expect.stringContains why "not tabulated" "the refusal names the gap"
          }

          test "the running process is tabulated" {
              Expect.isOk LinuxOpenFlags.current $"%A{RuntimeInformation.ProcessArchitecture} must be tabulated"
          } ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs
        []
        argv
        (testList
            "Fogell.Domain"
            [ aggregationProperties
              transitionProperties
              publicationGuard
              retrySemantics
              linuxOpenFlags
              RetryDecisionTests.tests ])
