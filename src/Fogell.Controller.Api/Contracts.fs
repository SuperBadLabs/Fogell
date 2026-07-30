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

/// Errors carry a stable code as well as a message, so a client can branch on
/// the code and show the message. ADR 0001 forbids an unnamed rejection.
type ErrorResponse =
    { [<JsonPropertyName "code">] Code: string
      [<JsonPropertyName "message">] Message: string
      [<JsonPropertyName "position">] Position: string option }
