module Fogell.Differential.TerminalOutputSettlementTests

open System
open Expecto
open Fogell.Differential
open Fogell.Execution

let terminalOutputSettlement =
    testList
        "terminal output settlement"
        [ test "runtime guard failure survives a sticky quota discovered during terminal settlement" {
              let guardFailure = RuntimeGuardFailure "test runtime drift"
              let ctx =
                  WalkerCtx.createWithOutputBudget
                      { MaxCharacters = 0L
                        MaxRecords = 10 }
                      0L
                      false
                      None

              Expect.throwsT<BuildOutputLimitExceededException>
                  (fun () -> ctx.ReserveCapturedOutput 1)
                  "the reduced real WalkerCtx budget is sticky before terminal settlement"

              try
                  FogellSide.settleTerminalOutput
                      (Some(guardFailure :> exn))
                      ctx.FlushOutput
                      ctx.CheckOutputBudget
                  failtest "terminal settlement should rethrow the stronger active failure"
              with :? RuntimeGuardFailure as actual ->
                  Expect.isTrue
                      (obj.ReferenceEquals(actual, guardFailure))
                      "runtime guard drift outranks WalkerCtx's sticky quota from FlushOutput"
          }

          test "publication uncertainty outranks an active runtime guard during terminal settlement" {
              let guardFailure = RuntimeGuardFailure "test runtime drift"
              let publicationFailure = OutputPublicationException("test publisher failed", InvalidOperationException())

              try
                  FogellSide.settleTerminalOutput
                      (Some(guardFailure :> exn))
                      (fun () -> raise publicationFailure)
                      ignore
                  failtest "terminal settlement should surface publication uncertainty"
              with :? OutputPublicationException as actual ->
                  Expect.isTrue
                      (obj.ReferenceEquals(actual, publicationFailure))
                      "publication uncertainty keeps its established precedence"
          }

          test "a clean body still surfaces a terminal sticky quota" {
              let quotaFailure = BuildOutputLimitExceededException()
              let mutable flushed = false

              try
                  FogellSide.settleTerminalOutput
                      None
                      (fun () -> flushed <- true)
                      (fun () -> raise quotaFailure)
                  failtest "a terminal quota cannot be converted into success"
              with :? BuildOutputLimitExceededException as actual ->
                  Expect.isTrue flushed "normal completion still settles accepted output first"
                  Expect.isTrue
                      (obj.ReferenceEquals(actual, quotaFailure))
                      "the original sticky quota remains observable at the run boundary"
          } ]
