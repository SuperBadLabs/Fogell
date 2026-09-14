module Fogell.Differential.BuildOutputBudgetTests

open System
open Expecto
open Fogell.Differential
open Fogell.Execution

/// These use WalkerCtx's friend-only constructor so the production 32 Mi
/// character / 100k-record guard stays an implementation detail.
let buildOutputBudget =
    let context characters records =
        WalkerCtx.createWithOutputBudget
            { MaxCharacters = characters
              MaxRecords = records }
            0L
            false
            None

    let buffered stream =
        stream.Buffered
        |> Option.defaultWith (fun () -> failtest "WalkerCtx stream exposes buffered admission credits")

    let close stream =
        stream.Close
        |> Option.defaultWith (fun () -> failtest "WalkerCtx stream exposes an owner closure lease")

    testList
        "whole-build output budget"
        [ test "logical records charge framed UTF-16 characters at the exact boundary" {
              let ctx = context 4L 2

              ctx.Emit "a"
              ctx.Emit "b"

              Expect.equal (ctx.Output()) [ "a"; "b" ] "two one-character records consume text plus LF framing"
              Expect.throwsT<BuildOutputLimitExceededException>
                  (fun () -> ctx.Emit "")
                  "the next framed record cannot exceed the character budget"
              Expect.isTrue (ctx.OutputBudgetExceeded()) "the capacity trip is sticky and pollable"
              Expect.throwsT<BuildOutputLimitExceededException>
                  ctx.CheckOutputBudget
                  "the sticky exception is safe to surface at the run boundary"
              Expect.equal (ctx.Output()) [ "a"; "b" ] "a refused record never changes retained output"
          }

          test "buffered framing transfers a completed line credit exactly once" {
              let ctx = context 2L 10
              let stream = ctx.CreateRedactedAdmission()
              let credits = buffered stream

              credits.Reserve 2
              credits.Admit 2 (RedactedText.Raw "x")

              Expect.equal (ctx.Output()) [ "x" ] "the line consumes its x plus framed terminator charge once"
              stream.Complete()
          }

          test "parallel partial streams cannot exceed the aggregate outstanding credit budget" {
              let ctx = context 5L 10
              let firstStream = ctx.CreateRedactedAdmission()
              let secondStream = ctx.CreateRedactedAdmission()
              let first = buffered firstStream
              let second = buffered secondStream
              let mutable admitted = 0

              System.Threading.Tasks.Parallel.Invoke(
                  Action(fun () ->
                      try
                          first.Reserve 3
                          System.Threading.Interlocked.Increment(&admitted) |> ignore
                      with :? BuildOutputLimitExceededException -> ()),
                  Action(fun () ->
                      try
                          second.Reserve 3
                          System.Threading.Interlocked.Increment(&admitted) |> ignore
                      with :? BuildOutputLimitExceededException -> ()))

              Expect.equal (System.Threading.Volatile.Read(&admitted)) 1 "only one partial stream fits"
              Expect.isTrue (ctx.OutputBudgetExceeded()) "the second reservation trips the shared sticky budget"
              // Cleanup stays possible after the sticky interrupt and returns
              // the winner's transient framing credit.
              if System.Threading.Volatile.Read(&admitted) = 1 then
                  try
                      first.Release 3
                  with :? ArgumentException -> second.Release 3
              firstStream.Complete()
              secondStream.Complete()
          }

          test "buffered admission rejects credit from another or larger stream" {
              let ctx = context 10L 10
              let firstStream = ctx.CreateRedactedAdmission()
              let secondStream = ctx.CreateRedactedAdmission()
              let first = buffered firstStream
              let second = buffered secondStream
              first.Reserve 1

              Expect.throwsT<ArgumentException>
                  (fun () -> first.Admit 2 (RedactedText.Raw "x"))
                  "a framer cannot turn more bytes into a record than its stream reserved"
              Expect.throwsT<ArgumentException>
                  (fun () -> second.Admit 1 (RedactedText.Raw "x"))
                  "a stream cannot spend a different stream's reservation"
              first.Release 1
              firstStream.Complete()
              secondStream.Complete()
          }

          test "buffered admission consumes its credit before a final record failure" {
              let ctx = context 10L 0
              let stream = ctx.CreateRedactedAdmission()
              let credits = buffered stream
              credits.Reserve 2

              Expect.throwsT<BuildOutputLimitExceededException>
                  (fun () -> credits.Admit 1 (RedactedText.Raw ""))
                  "the final record limit still applies after framing credit transfers"
              credits.Release 1
              Expect.throwsT<ArgumentException>
                  (fun () -> credits.Release 1)
                  "the failed final admission consumed exactly one credit and released remainder cannot be spent twice"
              stream.Complete()
          }

          test "closing a stream releases its own pending credits once and rejects all late admission" {
              let ctx = context 4L 10
              let firstStream = ctx.CreateRedactedAdmission()
              let secondStream = ctx.CreateRedactedAdmission()
              let first = buffered firstStream
              let second = buffered secondStream

              first.Reserve 4
              close firstStream ()
              close firstStream ()

              // A late reader can finish its local cleanup, but cannot reserve,
              // turn an arbitrary old credit into output, or claim EOF.
              first.Reserve 100
              first.Admit 100 (RedactedText.Raw "late")
              first.Release 100
              firstStream.Admit (RedactedText.Raw "late")
              firstStream.Complete()

              second.Reserve 4
              // The first stream's delayed release is a no-op: it must not
              // refund the second stream's full live reservation.
              first.Release 4
              Expect.throwsT<BuildOutputLimitExceededException>
                  (fun () -> ctx.ReserveCapturedOutput 1)
                  "a closed stream cannot double-refund credit now held by another stream"
              second.Release 4
              secondStream.Complete()
              Expect.isEmpty (ctx.Output()) "the closed stream cannot append late logical output"
          }

          test "late binding after close retains the incomplete publication barrier" {
              let published = ResizeArray<string>()
              let ctx =
                  WalkerCtx.createWithOutputBudget
                      { MaxCharacters = 100L
                        MaxRecords = 10 }
                      0L
                      false
                      (Some published.Add)
              let stream = ctx.CreateRedactedAdmission()

              close stream ()
              ctx.BindSecrets [ Secrets.inMemoryTextBinding "TOKEN" "secret" ]
              stream.Admit (RedactedText.Raw "late")
              stream.Complete()

              Expect.throwsT<OutputPublicationException>
                  ctx.FlushOutput
                  "closure without EOF keeps a later credential binding fail-closed"
              Expect.isEmpty published "an incomplete closed stream does not publish a fabricated EOF suffix"
          }

          test "a stream completed before closure remains publishable" {
              let published = ResizeArray<string>()
              let ctx =
                  WalkerCtx.createWithOutputBudget
                      { MaxCharacters = 100L
                        MaxRecords = 10 }
                      0L
                      false
                      (Some published.Add)
              let stream = ctx.CreateRedactedAdmission()

              stream.Admit (RedactedText.Raw "safe")
              stream.Complete()
              close stream ()
              ctx.BindSecrets [ Secrets.inMemoryTextBinding "TOKEN" "secret" ]
              ctx.FlushOutput()

              Expect.equal (List.ofSeq published) [ "safe" ] "only a real EOF clears the stream barrier"
          }

          test "CRLF's ignored LF releases its transient buffered credit" {
              let finalRecordCharacters = 1 + Environment.NewLine.Length
              let ctx = context (int64 (finalRecordCharacters + 1)) 10
              let stream = ctx.CreateRedactedAdmission()
              let credits = buffered stream

              // ProcessGroup reserves x, CR and LF; its framer transfers
              // through CR, then releases the ignored LF of the CRLF pair.
              credits.Reserve 3
              credits.Admit 2 (RedactedText.Raw "x")
              credits.Release 1
              credits.Reserve 1
              credits.Release 1
              stream.Complete()
          }

          test "record capacity is independent of available character capacity" {
              let ctx = context 1_000L 1
              ctx.Emit ""

              Expect.throwsT<BuildOutputLimitExceededException>
                  (fun () -> ctx.Emit "")
                  "a second empty framed line exceeds the logical-record cap, not the text cap"
          }

          test "parallel console admission and captured reservations share one atomic character bucket" {
              let ctx = context 20L 100
              let mutable captured = 0

              System.Threading.Tasks.Parallel.For(
                  0,
                  80,
                  Action<int>(fun index ->
                      try
                          if index % 2 = 0 then
                              ctx.Emit ""
                          else
                              ctx.ReserveCapturedOutput 1
                              System.Threading.Interlocked.Increment(&captured) |> ignore
                      with :? BuildOutputLimitExceededException -> ()))
              |> ignore

              // Each successful operation charges exactly one character: the
              // empty console line's LF or one captured UTF-16 code unit.
              Expect.equal
                  (ctx.Output().Length + System.Threading.Volatile.Read(&captured))
                  20
                  "the serialised reservation boundary admits neither a lost nor an over-limit unit"
              Expect.isTrue (ctx.OutputBudgetExceeded()) "one loser trips the shared sticky limit"
          }

          test "timestamps consume their generated prefix in the shared text charge" {
              let timestampPrefixLength = "[0000-00-00T00:00:00.000Z] ".Length
              let ctx = context (int64 (timestampPrefixLength + Environment.NewLine.Length)) 10
              ctx.EnableTimestamps()
              ctx.Emit ""

              Expect.equal (ctx.Output() |> List.exactlyOne |> _.Length) timestampPrefixLength "the stored record has the engine prefix"
              Expect.throwsT<BuildOutputLimitExceededException>
                  (fun () -> ctx.ReserveCapturedOutput 1)
                  "the timestamp prefix and record delimiter leave no hidden character capacity"
          }

          test "captured stdout shares the character budget but creates no logical record" {
              let ctx = context 5L 1

              ctx.ReserveCapturedOutput 3
              ctx.Emit "a"

              Expect.equal (ctx.Output()) [ "a" ] "capture reservation leaves one framed console record"
              Expect.throwsT<BuildOutputLimitExceededException>
                  (fun () -> ctx.ReserveCapturedOutput 1)
                  "capture is charged cumulatively with console text"
              Expect.equal (ctx.Output()) [ "a" ] "capture exhaustion does not invent an output record"
          }

          test "late stream remasking charges positive growth without a refund" {
              // The original x consumes x + LF = 2.  Replacing a one-character
              // secret with the four-character canonical token needs three
              // additional retained characters and must trip this four-unit cap.
              let ctx = context 4L 10
              let stream = ctx.CreateRedactedAdmission()
              stream.Admit (RedactedText.Raw "x")
              ctx.BindSecrets [ Secrets.inMemoryTextBinding "TOKEN" "x" ]
              // A fragment admitted after the binding makes the still-open
              // stream reach its EOF remask path.  It is a logical record too.
              stream.Admit (RedactedText.Raw "")
              stream.Complete()
              Expect.isTrue (ctx.OutputBudgetExceeded()) "a remask cannot expand retained provenance outside the run budget"
              Expect.throwsT<BuildOutputLimitExceededException>
                  ctx.FlushOutput
                  "EOF remains usable after exhaustion and flush reports the original guard"
          }

          test "EOF drains an already admitted barrier suffix after an unrelated quota trip" {
              let published = ResizeArray<string>()
              let ctx =
                  WalkerCtx.createWithOutputBudget
                      { MaxCharacters = 7L
                        MaxRecords = 10 }
                      0L
                      false
                      (Some published.Add)
              let stream = ctx.CreateRedactedAdmission()
              stream.Admit (RedactedText.Raw "safe")
              ctx.BindSecrets [ Secrets.inMemoryTextBinding "UNRELATED" "secret" ]
              stream.Admit (RedactedText.Raw "")

              Expect.throwsT<BuildOutputLimitExceededException>
                  (fun () -> ctx.ReserveCapturedOutput 2)
                  "capture exhausts the shared budget while the stream waits for EOF"

              stream.Complete()

              Expect.throwsT<BuildOutputLimitExceededException>
                  ctx.FlushOutput
                  "flush drains the safe suffix first and then reports capacity"
              Expect.equal
                  (List.ofSeq published)
                  [ "safe"; "" ]
                  "EOF remasking without growth retains the already admitted FIFO suffix"
          }

          test "a blocked publisher retains accepted FIFO records and drains them before budget failure" {
              use callbackEntered = new Threading.ManualResetEventSlim(false)
              use releaseCallback = new Threading.ManualResetEventSlim(false)
              let published = ResizeArray<string>()
              let ctx =
                  WalkerCtx.createWithOutputBudget
                      { MaxCharacters = 8L
                        MaxRecords = 10 }
                      0L
                      false
                      (Some(fun line ->
                          published.Add line
                          if line = "one" then
                              callbackEntered.Set()
                              releaseCallback.Wait()))
              let first = System.Threading.Tasks.Task.Run(fun () -> ctx.Emit "one")

              try
                  Expect.isTrue (callbackEntered.Wait 2_000) "the publication prefix is genuinely in flight"
                  ctx.Emit "two"
                  Expect.throwsT<BuildOutputLimitExceededException>
                      (fun () -> ctx.ReserveCapturedOutput 1)
                      "the full retained text bucket rejects later capture"
              finally
                  releaseCallback.Set()

              first.GetAwaiter().GetResult()
              Expect.throwsT<BuildOutputLimitExceededException>
                  ctx.FlushOutput
                  "flush drains accepted backlog before returning the sticky quota error"
              Expect.equal (List.ofSeq published) [ "one"; "two" ] "accepted publication order remains FIFO"
          }

          test "late credential binding remasks a stalled publication queue after another branch exhausts budget" {
              use callbackEntered = new Threading.ManualResetEventSlim(false)
              use releaseCallback = new Threading.ManualResetEventSlim(false)
              let published = ResizeArray<string>()
              let ctx =
                  WalkerCtx.createWithOutputBudget
                      { MaxCharacters = 10L
                        MaxRecords = 10 }
                      0L
                      false
                      (Some(fun line ->
                          published.Add line
                          if line = "blocker" then
                              callbackEntered.Set()
                              releaseCallback.Wait()))
              let blocker = System.Threading.Tasks.Task.Run(fun () -> ctx.Emit "blocker")

              try
                  Expect.isTrue (callbackEntered.Wait 2_000) "the accepted prefix is genuinely in the callback"
                  ctx.Emit "x"
                  Expect.throwsT<BuildOutputLimitExceededException>
                      (fun () -> ctx.ReserveCapturedOutput 1)
                      "a concurrent capture trip exhausts the shared quota before credential registration"
                  Expect.throwsT<BuildOutputLimitExceededException>
                      (fun () -> ctx.BindSecrets [ Secrets.inMemoryTextBinding "TOKEN" "x" ])
                      "late remasking cannot spend unavailable growth"
              finally
                  releaseCallback.Set()

              blocker.GetAwaiter().GetResult()
              Expect.throwsT<BuildOutputLimitExceededException>
                  ctx.FlushOutput
                  "the retained prefix drains before the sticky quota reaches the terminal boundary"
              Expect.equal (List.ofSeq published) [ "blocker" ] "the unsafe queued raw line is withheld"
              Expect.equal (ctx.Output()) [ "blocker" ] "the trace cannot expose the stale pre-binding raw representation"
          }

          test "a callback that directly rethrows its reentrant quota trip is publication uncertainty" {
              let mutable reserve = ignore
              let ctx =
                  WalkerCtx.createWithOutputBudget
                      { MaxCharacters = 2L
                        MaxRecords = 1 }
                      0L
                      false
                      (Some(fun _ -> reserve 1))
              reserve <- ctx.ReserveCapturedOutput

              Expect.throwsT<OutputPublicationException>
                  (fun () -> ctx.Emit "a")
                  "the callback has not proved persistence of its current record"
              Expect.throwsT<OutputPublicationException>
                  ctx.FlushOutput
                  "the ambiguous callback prefix keeps publication failure precedence"
          }

          test "a failed asynchronous publisher does not stop later deferred raw admission" {
              let ctx =
                  WalkerCtx.createWithOutputBudget
                      { MaxCharacters = 100L
                        MaxRecords = 10 }
                      0L
                      false
                      (Some(fun _ -> raise (IO.IOException "event sink unavailable")))

              Expect.throwsT<OutputPublicationException>
                  (fun () -> ctx.Emit "one")
                  "the publisher failure is sticky"

              let stream = ctx.CreateRedactedAdmission()
              stream.Admit (RedactedText.Raw "reader-keeps-draining")
              stream.Complete()

              Expect.equal
                  (ctx.Output())
                  [ "one"; "reader-keeps-draining" ]
                  "deferred raw admission keeps process-reader output despite a broken publisher"
          }

          test "a capacity attempt after publisher failure latches the global budget while surfacing publication uncertainty" {
              let ctx =
                  WalkerCtx.createWithOutputBudget
                      { MaxCharacters = 4L
                        MaxRecords = 10 }
                      0L
                      false
                      (Some(fun _ -> raise (IO.IOException "event sink unavailable")))

              Expect.throwsT<OutputPublicationException> (fun () -> ctx.Emit "one") "the initial callback failed"
              let stream = ctx.CreateRedactedAdmission()
              Expect.throwsT<OutputPublicationException>
                  (fun () -> stream.Admit (RedactedText.Raw "x"))
                  "publication uncertainty remains the caller-visible failure"
              Expect.isTrue (ctx.OutputBudgetExceeded()) "the failed reservation still interrupts sibling work"
              stream.Complete()
          }

          test "an over-capacity capture attempt latches budget after publisher failure" {
              let ctx =
                  WalkerCtx.createWithOutputBudget
                      { MaxCharacters = 4L
                        MaxRecords = 10 }
                      0L
                      false
                      (Some(fun _ -> raise (IO.IOException "event sink unavailable")))

              Expect.throwsT<OutputPublicationException> (fun () -> ctx.Emit "one") "the initial callback failed"
              Expect.throwsT<OutputPublicationException>
                  (fun () -> ctx.ReserveCapturedOutput 1)
                  "host reconciliation remains authoritative to the capture caller"
              Expect.isTrue (ctx.OutputBudgetExceeded()) "the rejected capture still broadcasts whole-build exhaustion"
          }

          test "an accepted publication failure remains authoritative over a callback reentry budget trip" {
              let mutable reserve = ignore
              let ctx =
                  WalkerCtx.createWithOutputBudget
                      { MaxCharacters = 2L
                        MaxRecords = 1 }
                      0L
                      false
                      (Some(fun _ ->
                          try
                              reserve 1
                          with :? BuildOutputLimitExceededException ->
                              raise (IO.IOException "host failed after accepted output")))

              reserve <- ctx.ReserveCapturedOutput

              Expect.throwsT<OutputPublicationException>
                  (fun () -> ctx.Emit "a")
                  "the callback's real transport loss is not relabelled as capacity"
              Expect.throwsT<OutputPublicationException>
                  ctx.FlushOutput
                  "flush preserves publication failure precedence over the sticky budget"
          } ]
