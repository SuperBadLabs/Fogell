namespace Fogell.Controller.Host

open System
open System.IO

type internal EventFrame =
    | Encoded of byte array
    | Oversized

type internal EventDrainState =
    { mutable Offset: int64
      mutable Tail: byte array
      mutable DiscardingOversizedFrame: bool }

type internal EventDrainBatch =
    { BytesProcessed: int
      FramesProcessed: int
      ReachedEof: bool
      AuthorityLost: bool }

type internal EventDrainStop =
    | EndOfStream
    | ControlStopped
    | PublicationAuthorityLost
    | StreamChangedAfterExtinction
    | IncompleteFrameAtEndOfStream

type internal EventDrainCompletion =
    { BytesProcessed: int
      FramesProcessed: int
      Stop: EventDrainStop }

module internal EventStream =

    let private strictUtf8 = Text.UTF8Encoding(false, true)

    /// Malformed payloads must not expand through replacement characters and
    /// exceed the decoded UTF-8 batch bound.
    let eventBody frame =
        match frame with
        | Oversized -> "controller refused an oversized child log frame"
        | Encoded bytes ->
            let payloadLength =
                if bytes.Length > 0 && bytes[bytes.Length - 1] = byte '\r' then
                    bytes.Length - 1
                else
                    bytes.Length

            try
                Text.Encoding.ASCII.GetString(bytes, 0, payloadLength)
                |> Convert.FromBase64String
                |> strictUtf8.GetString
            with _ ->
                "controller refused a malformed child log frame"

    /// Consume a bounded prefix of an append-only, newline-framed stream.
    /// Offset advances only for bytes actually inspected, so bytes read ahead
    /// into the local buffer are safely re-read by the next batch. The callback
    /// returns false when the caller's fence/lease no longer authorizes output;
    /// the batch then stops immediately.
    let drainBatch
        (stream: Stream)
        (state: EventDrainState)
        maxFrameBytes
        byteBudget
        frameBudget
        (publish: EventFrame -> bool)
        =
        if maxFrameBytes < 1 then invalidArg (nameof maxFrameBytes) "frame bound must be positive"
        if byteBudget < 1 then invalidArg (nameof byteBudget) "byte budget must be positive"
        if frameBudget < 1 then invalidArg (nameof frameBudget) "frame budget must be positive"
        if not stream.CanRead || not stream.CanSeek then invalidArg (nameof stream) "event stream must be readable and seekable"
        if stream.Length < state.Offset then invalidOp "child event file was truncated after consumption"

        stream.Position <- state.Offset
        use frame = new MemoryStream()
        frame.Write(state.Tail, 0, state.Tail.Length)
        let buffer = Array.zeroCreate<byte> (min (64 * 1024) byteBudget)
        let mutable bytesProcessed = 0
        let mutable framesProcessed = 0
        let mutable authorityLost = false
        let mutable reading = true
        let mutable reachedEof = false

        while reading && not authorityLost && bytesProcessed < byteBudget && framesProcessed < frameBudget do
            let wanted = min buffer.Length (byteBudget - bytesProcessed)
            let count = stream.Read(buffer, 0, wanted)

            if count = 0 then
                reading <- false
                reachedEof <- true
            else
                let mutable index = 0

                while index < count && not authorityLost && framesProcessed < frameBudget do
                    let value = buffer[index]
                    index <- index + 1

                    if value = byte '\n' then
                        let item =
                            if state.DiscardingOversizedFrame then
                                Oversized
                            else
                                Encoded(frame.ToArray())

                        if publish item then
                            // The newline linearizes the frame. Do not consume
                            // it, clear the retained payload, or leave discard
                            // mode until the fenced append has been accepted.
                            bytesProcessed <- bytesProcessed + 1
                            state.Offset <- state.Offset + 1L
                            frame.SetLength 0L
                            state.DiscardingOversizedFrame <- false
                            framesProcessed <- framesProcessed + 1
                        else
                            authorityLost <- true
                    elif not state.DiscardingOversizedFrame then
                        bytesProcessed <- bytesProcessed + 1
                        state.Offset <- state.Offset + 1L

                        if frame.Length >= int64 maxFrameBytes then
                            frame.SetLength 0L
                            state.DiscardingOversizedFrame <- true
                        else
                            frame.WriteByte value
                    else
                        bytesProcessed <- bytesProcessed + 1
                        state.Offset <- state.Offset + 1L

        state.Tail <- frame.ToArray()

        { BytesProcessed = bytesProcessed
          FramesProcessed = framesProcessed
          // Equality with Length is not EOF: the writer may append between the
          // observation and the next poll. Only an actual zero-byte read closes
          // a post-exit drain.
          ReachedEof = reachedEof
          AuthorityLost = authorityLost }

    /// Stage parsing in a private cursor. A store refusal or exception must
    /// not acknowledge any frame in an atomic batch, including a retained
    /// prefix or oversized-frame discard state from an earlier slice.
    let drainCommittedBatch stream (state: EventDrainState) maxFrameBytes byteBudget frameBudget publish =
        let staged = { state with Tail = Array.copy state.Tail }
        let frames = ResizeArray<EventFrame>()
        let batch = drainBatch stream staged maxFrameBytes byteBudget frameBudget (fun frame ->
            frames.Add frame
            true)

        if frames.Count = 0 || publish (frames.ToArray()) then
            state.Offset <- staged.Offset
            state.Tail <- staged.Tail
            state.DiscardingOversizedFrame <- staged.DiscardingOversizedFrame
            batch
        else
            { BytesProcessed = 0
              FramesProcessed = 0
              ReachedEof = false
              AuthorityLost = true }

    /// Drain an immutable finite boundary with bounded slices and cooperative
    /// scheduling. Both publication modes share the same extinction checks.
    let private drainExtinguishedBoundaryUsing
        yieldBetweenBatches
        (drainSlice: Stream -> EventDrainState -> int -> int -> int -> EventDrainBatch)
        (openStream: unit -> Stream option)
        (state: EventDrainState)
        maxFrameBytes
        sliceByteBudget
        sliceFrameBudget
        (continueControl: unit -> bool)
        =
        task {
            let mutable totalBytes = 0
            let mutable totalFrames = 0
            let mutable stop = None

            match openStream () with
            | None ->
                stop <-
                    if state.Offset <> 0L then
                        Some StreamChangedAfterExtinction
                    elif state.Tail.Length <> 0 || state.DiscardingOversizedFrame then
                        Some IncompleteFrameAtEndOfStream
                    else
                        Some EndOfStream
            | Some opened ->
                // Retain the same handle for the full drain. Besides fixing the
                // byte boundary, this fixes file identity if the path is replaced.
                use stream = opened
                let boundary = stream.Length

                if boundary < state.Offset then
                    stop <- Some StreamChangedAfterExtinction

                while stop.IsNone do
                    // Even a continuously full file gives request handling and
                    // shutdown continuations an opportunity to run between slices.
                    if yieldBetweenBatches then
                        do! System.Threading.Tasks.Task.Yield()
                    if not (continueControl()) then
                        stop <- Some ControlStopped
                    elif stream.Length <> boundary then
                        stop <- Some StreamChangedAfterExtinction
                    elif state.Offset = boundary then
                        stop <-
                            if state.Tail.Length = 0 && not state.DiscardingOversizedFrame then
                                Some EndOfStream
                            else
                                Some IncompleteFrameAtEndOfStream
                    else
                        let remaining = boundary - state.Offset
                        let batch =
                            drainSlice
                                stream
                                state
                                maxFrameBytes
                                (min sliceByteBudget (int (min remaining (int64 Int32.MaxValue))))
                                sliceFrameBudget

                        totalBytes <- totalBytes + batch.BytesProcessed
                        totalFrames <- totalFrames + batch.FramesProcessed

                        if batch.AuthorityLost then
                            stop <- Some PublicationAuthorityLost
                        elif batch.BytesProcessed = 0 then
                            // The frozen boundary promised unread bytes. A zero
                            // read cannot safely be interpreted as completion.
                            stop <- Some StreamChangedAfterExtinction

            return { BytesProcessed = totalBytes
                     FramesProcessed = totalFrames
                     Stop = stop.Value }

        }

    /// Single-frame publication retained for existing callers and proofs.
    let drainExtinguishedBoundary openStream state maxFrameBytes sliceByteBudget sliceFrameBudget continueControl publish =
        let drain stream cursor maximum bytes frames = drainBatch stream cursor maximum bytes frames publish
        (drainExtinguishedBoundaryUsing false drain openStream state maxFrameBytes sliceByteBudget sliceFrameBudget continueControl)
            .GetAwaiter().GetResult()

    /// Production publication commits one bounded batch before advancing its
    /// cursor. No cumulative cap can discard a finite post-exit log tail.
    let drainExtinguishedBoundaryBatched openStream state maxFrameBytes sliceByteBudget sliceFrameBudget continueControl publish =
        let drain stream cursor maximum bytes frames = drainCommittedBatch stream cursor maximum bytes frames publish
        drainExtinguishedBoundaryUsing true drain openStream state maxFrameBytes sliceByteBudget sliceFrameBudget continueControl

    /// Terminal truth is authorized only by a complete, immutable frame
    /// boundary. Every other completion requires reconciliation.
    let terminalPublicationAllowed completion =
        completion.Stop = EndOfStream
