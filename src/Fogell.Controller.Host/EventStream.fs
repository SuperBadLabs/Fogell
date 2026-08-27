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

    /// Drain the exact finite boundary observed after producer extinction.
    /// Individual slices remain bounded so lease/cancellation checks are
    /// interleaved with publication, but there is deliberately no cumulative
    /// byte or frame ceiling: a finite tail must not be silently truncated.
    /// Any later length change contradicts the extinction proof and is surfaced
    /// as unsafe evidence rather than being folded into terminal truth.
    let drainExtinguishedBoundary
        (openStream: unit -> Stream option)
        (state: EventDrainState)
        maxFrameBytes
        sliceByteBudget
        sliceFrameBudget
        (continueControl: unit -> bool)
        publish
        =
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
                        drainBatch
                            stream
                            state
                            maxFrameBytes
                            (min sliceByteBudget (int (min remaining (int64 Int32.MaxValue))))
                            sliceFrameBudget
                            publish

                    totalBytes <- totalBytes + batch.BytesProcessed
                    totalFrames <- totalFrames + batch.FramesProcessed

                    if batch.AuthorityLost then
                        stop <- Some PublicationAuthorityLost
                    elif batch.BytesProcessed = 0 then
                        // The frozen boundary promised unread bytes. A zero
                        // read cannot safely be interpreted as completion.
                        stop <- Some StreamChangedAfterExtinction

        { BytesProcessed = totalBytes
          FramesProcessed = totalFrames
          Stop = stop.Value }

    /// Terminal truth is authorized only by a complete, immutable frame
    /// boundary. Every other completion requires reconciliation.
    let terminalPublicationAllowed completion =
        completion.Stop = EndOfStream
