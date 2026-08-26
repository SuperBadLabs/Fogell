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
    | CumulativeBudgetExhausted
    | ControlStopped
    | PublicationAuthorityLost

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

    /// Repeated bounded slices for the post-exit phase. The caller supplies a
    /// fresh stream view and a control check before every slice. Completion is
    /// bounded even if another writer keeps appending forever.
    let drainToBoundary
        (openStream: unit -> Stream option)
        (state: EventDrainState)
        maxFrameBytes
        sliceByteBudget
        sliceFrameBudget
        cumulativeByteBudget
        cumulativeFrameBudget
        (continueControl: unit -> bool)
        publish
        =
        if cumulativeByteBudget < 1 then
            invalidArg (nameof cumulativeByteBudget) "cumulative byte budget must be positive"

        if cumulativeFrameBudget < 1 then
            invalidArg (nameof cumulativeFrameBudget) "cumulative frame budget must be positive"

        let mutable totalBytes = 0
        let mutable totalFrames = 0
        let mutable stop = None

        while stop.IsNone do
            if not (continueControl()) then
                stop <- Some ControlStopped
            elif totalBytes >= cumulativeByteBudget || totalFrames >= cumulativeFrameBudget then
                stop <- Some CumulativeBudgetExhausted
            else
                match openStream () with
                | None -> stop <- Some EndOfStream
                | Some stream ->
                    use stream = stream
                    let batch =
                        drainBatch
                            stream
                            state
                            maxFrameBytes
                            (min sliceByteBudget (cumulativeByteBudget - totalBytes))
                            (min sliceFrameBudget (cumulativeFrameBudget - totalFrames))
                            publish

                    totalBytes <- totalBytes + batch.BytesProcessed
                    totalFrames <- totalFrames + batch.FramesProcessed

                    if batch.AuthorityLost then
                        stop <- Some PublicationAuthorityLost
                    elif batch.ReachedEof then
                        stop <- Some EndOfStream

        { BytesProcessed = totalBytes
          FramesProcessed = totalFrames
          Stop = stop.Value }
