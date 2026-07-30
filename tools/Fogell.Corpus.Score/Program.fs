module Fogell.Corpus.Score.Program

open System.IO
open Fogell.Admission
open Fogell.Pipeline.Parser

/// Scores the declarative parser against the pinned corpus. Parse only — this
/// never executes corpus code. Emits TSV so the result can be diffed between
/// runs and sealed as evidence (FG-005).
[<EntryPoint>]
let main argv =
    let corpus =
        match Array.tryHead argv with
        | Some p -> p
        | None ->
            match System.Environment.GetEnvironmentVariable "FOGELL_CORPUS" with
            | null
            | "" -> "/sn8100/work/exchange/crucible-gate/corpus"
            | v -> v

    let dir = Path.Combine(corpus, "jenkinsfiles")

    if not (Directory.Exists dir) then
        eprintfn $"corpus not found: {dir}"
        2
    else

    printfn "file\tverdict\tcode\tstages\tsteps\tdetail"

    Directory.GetFiles(dir, "*.Jenkinsfile")
    |> Array.sort
    |> Array.iter (fun path ->
        let name = Path.GetFileName path
        let src = File.ReadAllText path

        if not (Parser.looksDeclarative src) then
            // scripted path: the Groovy escape hatch (ADR 0002)
            match Fogell.Groovy.Parser.Parser.parse src with
            | Ok script ->
                let stmts = Fogell.Groovy.Ast.countStmts script
                let calls = Fogell.Groovy.Ast.freeCalls script |> Set.count
                printfn $"{name}\tscripted-ok\t-\t{stmts}\t{calls}\t-"
            | Error e ->
                let d = e.Message.Replace("\t", " ")
                printfn $"{name}\tscripted-err\t{ErrorCode.toWireString e.Code}\t-\t-\t{d} @{e.Position}"
        else
            match Parser.parse src with
            | Ok p ->
                let stages = Fogell.Ir.Pipeline.flattenStages p.Stages |> List.length
                let steps = Fogell.Ir.Pipeline.totalSteps p
                printfn $"{name}\tok\t-\t{stages}\t{steps}\t-"
            | Error e ->
                let detail = e.Message.Replace("\t", " ")
                printfn $"{name}\terr\t{ErrorCode.toWireString e.Code}\t-\t-\t{detail} @{e.Position}")

    0
