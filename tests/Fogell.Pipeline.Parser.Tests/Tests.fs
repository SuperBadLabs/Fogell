module Fogell.Pipeline.Parser.Tests

open Expecto
open System.Text.Json
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
    test "environment names accept ASCII identifier boundaries at root and step scope" {
        let source = """{"version":1,"env":{"_A9":"root","Z_0":"also root"},"stages":[{"name":"build","steps":[{"echo":"ok","env":{"_STEP9":"step","S_0":"also step"}}]}]}"""
        match Parser.parse source with
        | Error e -> failtest (string e)
        | Ok pipeline ->
            Expect.equal pipeline.Environment ["_A9", "root"; "Z_0", "also root"] "root identifiers"
            Expect.equal pipeline.Stages.Head.Steps.Head.Environment ["_STEP9", "step"; "S_0", "also step"] "step identifiers"
    }
    testList "environment names reject trailing and control characters at both scopes" [
        for scope in [ "pipeline"; "step" ] do
            for suffix, invalidChar in [ "LF", "\n"; "CRLF", "\r\n"; "tab", "\t"; "DEL", "\u007f"; "non-ASCII", "é"; "punctuation", "-" ] do
                test $"{scope} {suffix}" {
                    let invalidEnv = "\"env\":{" + JsonSerializer.Serialize("NAME" + invalidChar) + ":\"x\"},"
                    let source =
                        if scope = "pipeline" then
                            "{\"version\":1," + invalidEnv + "\"stages\":[{\"name\":\"build\",\"steps\":[{\"echo\":\"ok\"}]}]}"
                        else
                            "{\"version\":1,\"stages\":[{\"name\":\"build\",\"steps\":[{\"echo\":\"ok\"," + invalidEnv + "}]}]}"
                    match Parser.parse source with
                    | Ok _ -> failtest "unexpected admission"
                    | Error error -> Expect.equal error.Code MalformedSyntax "invalid environment name refused"
                }
    ]
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
