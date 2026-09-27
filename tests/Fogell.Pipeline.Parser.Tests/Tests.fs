module Fogell.Pipeline.Parser.Tests

open Expecto
open Fogell.Admission
open Fogell.Pipeline.Parser

let valid = """{"version":1,"env":{"MODE":"test"},"stages":[{"name":"build","steps":[{"run":"dotnet build"},{"test_report":"reports/*.xml","always":true}]}]}"""
let tests = testList "native pipeline admission" [
    test "admits versioned typed operations" {
        match Parser.parse valid with
        | Error e -> failtest (string e)
        | Ok pipeline ->
            Expect.equal pipeline.Version 1 "version"
            Expect.equal pipeline.Stages.Head.Steps.Length 2 "steps"
            Expect.equal pipeline.Environment ["MODE","test"] "literal environment"
    }
    testList "rejects before execution" [
        for name, source, code in [
            "unknown version", valid.Replace("\"version\":1","\"version\":2"), UnsupportedConstruct
            "duplicate key", valid.Replace("\"version\":1","\"version\":1,\"version\":1"), DuplicateSection
            "unknown field", valid.Replace("\"version\":1","\"version\":1,\"agent\":\"any\""), UnknownSection
            "empty stages", """{"version":1,"stages":[]}""", NoStages
            "duplicate stages", """{"version":1,"stages":[{"name":"a","steps":[{"echo":"a"}]},{"name":"a","steps":[{"echo":"b"}]}]}""", DuplicateSection
            "control in name", valid.Replace("\"name\":\"build\"","\"name\":\"bad\\tname\""), ExpectedStage
            "unknown operation", valid.Replace("\"run\":","\"execute_other\":"), UnknownSection
            "multiple operations", valid.Replace("\"run\":\"dotnet build\"","\"run\":\"build\",\"echo\":\"hello\""), ExpectedSteps
            "invalid timeout", valid.Replace("\"run\":\"dotnet build\"","\"run\":\"build\",\"timeout_seconds\":0"), MalformedSyntax
            "timeout type", valid.Replace("\"run\":\"dotnet build\"","\"run\":\"build\",\"timeout_seconds\":\"1\""), MalformedSyntax
            "traversal", valid.Replace("\"run\":\"dotnet build\"","\"run\":\"build\",\"working_directory\":\"../escape\""), MalformedSyntax
            "reserved environment", valid.Replace("MODE","FOGELL_API_TOKEN"), UnsupportedConstruct
            "non-JSON", "not JSON", MalformedSyntax
        ] do
            test name {
                match Parser.parse source with
                | Ok _ -> failtest "unexpected admission"
                | Error e -> Expect.equal e.Code code "named rejection"
            }
    ]
    test "source and scalar limits are enforced" {
        let limits={Limits.defaults with MaxSourceBytes=10}
        Expect.equal (Parser.parseWithLimits limits valid |> Result.mapError _.Code) (Error SourceTooLarge) "source bytes"
        let limits={Limits.defaults with MaxScalarBytes=4}
        Expect.equal (Parser.parseWithLimits limits valid |> Result.mapError _.Code) (Error ScalarTooLong) "scalar bytes"
    }
]
[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args tests
