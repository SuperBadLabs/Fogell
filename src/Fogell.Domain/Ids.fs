namespace Fogell.Domain

open System

/// Single-case wrappers so a build id can never be passed where a node id is
/// expected. Cheap at runtime, and the compiler catches the whole class of
/// argument-transposition bug that composite foreign keys catch in SQL.
[<Struct>]
type OrganizationId =
    | OrganizationId of Guid
    member this.Value = let (OrganizationId v) = this in v

[<Struct>]
type ProjectId =
    | ProjectId of Guid
    member this.Value = let (ProjectId v) = this in v

[<Struct>]
type BuildId =
    | BuildId of Guid
    member this.Value = let (BuildId v) = this in v

[<Struct>]
type NodeId =
    | NodeId of Guid
    member this.Value = let (NodeId v) = this in v

[<Struct>]
type AttemptId =
    | AttemptId of Guid
    member this.Value = let (AttemptId v) = this in v

/// Monotonic guard on an attempt. Only the holder of the current fence may
/// publish a terminal result; a stale holder is rejected rather than ignored.
[<Struct>]
type Fence =
    | Fence of int64
    member this.Value = let (Fence v) = this in v
    static member initial = Fence 0L
    member this.next = let (Fence v) = this in Fence(v + 1L)

/// Incremented once per controller restore. Invalidates every lease issued
/// before the restore so a pre-restore agent cannot publish.
[<Struct>]
type RestoreEpoch =
    | RestoreEpoch of int64
    member this.Value = let (RestoreEpoch v) = this in v
    static member initial = RestoreEpoch 0L
