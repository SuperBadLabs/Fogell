namespace Fogell.Controller.Api

open System.Text.Json.Serialization

/// The public wire contract. Field names are snake_case and stable: renaming one
/// is a breaking change to every client, so they are written explicitly rather
/// than derived from F# record names.
type AdmissionResponse =
    { [<JsonPropertyName "build_id">] BuildId: string
      [<JsonPropertyName "node_id">] NodeId: string
      [<JsonPropertyName "attempt_id">] AttemptId: string
      [<JsonPropertyName "number">] Number: int
      /// True when this submission matched an existing idempotency key. Clients
      /// need to distinguish "created" from "already existed" without comparing
      /// timestamps.
      [<JsonPropertyName "was_existing">] WasExisting: bool }

type StatusResponse =
    { [<JsonPropertyName "build_id">] BuildId: string
      [<JsonPropertyName "status">] Status: string
      [<JsonPropertyName "cancellation_requested">] CancellationRequested: bool }

type LogChunk =
    { [<JsonPropertyName "sequence">] Sequence: int
      [<JsonPropertyName "body">] Body: string }

type LogResponse =
    { [<JsonPropertyName "build_id">] BuildId: string
      [<JsonPropertyName "from_sequence">] FromSequence: int
      /// Present so a client can tail: poll again from this value.
      [<JsonPropertyName "next_sequence">] NextSequence: int
      [<JsonPropertyName "chunks">] Chunks: LogChunk list }

type ExplainResponse =
    { [<JsonPropertyName "trust_pool">] TrustPool: string
      [<JsonPropertyName "capabilities">] Capabilities: string list
      [<JsonPropertyName "explanation">] Explanation: string }

/// FG-026b. One external effect whose outcome the controller cannot know.
/// Read-only: the API lists uncertainty for an operator, it never replays,
/// resolves, or dismisses it.
type UncertainEffect =
    { [<JsonPropertyName "attempt_id">] AttemptId: string
      [<JsonPropertyName "effect_key">] EffectKey: string
      [<JsonPropertyName "fence">] Fence: int64
      [<JsonPropertyName "authority_owner">] AuthorityOwner: string
      [<JsonPropertyName "restore_epoch">] RestoreEpoch: int64
      [<JsonPropertyName "payload_sha256">] PayloadSha256: string
      /// "prepared" when the invocation may never have happened, "applied"
      /// when it did but its confirmation was lost.
      [<JsonPropertyName "uncertain_from">] UncertainFrom: string }

type UncertainEffectsResponse =
    { [<JsonPropertyName "organization_id">] OrganizationId: string
      [<JsonPropertyName "effects">] Effects: UncertainEffect list
      /// Opaque keyset cursor for `?cursor=` on the next request; null on the
      /// last page. Bound to the organization it was issued for.
      [<JsonPropertyName "next_cursor">] NextCursor: string option }

/// Errors carry a stable code as well as a message, so a client can branch on
/// the code and show the message. ADR 0001 forbids an unnamed rejection.
type ErrorResponse =
    { [<JsonPropertyName "code">] Code: string
      [<JsonPropertyName "message">] Message: string
      [<JsonPropertyName "position">] Position: string option }
