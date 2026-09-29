namespace Fogell.Pipeline.Parser

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Fogell.Admission
open Fogell.Ir

/// Strict native JSON admission. Unknown fields and duplicate keys are errors.
module Parser =
    exception private Invalid of ErrorCode * string
    let private refuse code message = raise (Invalid(code, message))
    let private fields allowed (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then refuse MalformedSyntax "expected an object"
        let seen = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        for property in value.EnumerateObject() do
            if not (seen.Add property.Name) then refuse DuplicateSection ("duplicate field: " + property.Name)
            if not (List.contains property.Name allowed) then refuse UnknownSection ("unknown field: " + property.Name)
    let private optional name (value: JsonElement) =
        match value.TryGetProperty(name: string) with true, item -> Some item | _ -> None
    let private required name value =
        optional name value |> Option.defaultWith (fun () -> refuse MalformedSyntax ("missing field: " + name))
    let private text (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.String then refuse MalformedSyntax "expected a string"
        let value = value.GetString()
        if value.Contains '\000' then refuse MalformedSyntax "NUL is not allowed in strings"
        value
    let private environment value =
        match optional "env" value with
        | None -> []
        | Some env ->
            if env.ValueKind <> JsonValueKind.Object then refuse MalformedSyntax "env must be an object"
            let names = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            [ for property in env.EnumerateObject() do
                if not (names.Add property.Name) then refuse DuplicateSection "duplicate environment name"
                // `$` also matches immediately before a final newline. Environment
                // names must end at the actual end of the JSON property string.
                if not (Regex.IsMatch(property.Name, "^[A-Za-z_][A-Za-z0-9_]*\\z")) then
                    refuse MalformedSyntax "invalid environment name"
                if property.Name.StartsWith("FOGELL_", StringComparison.Ordinal) then
                    refuse UnsupportedConstruct "FOGELL_ environment names are reserved"
                yield property.Name, text property.Value ]
    let private array name value =
        let items = required name value
        if items.ValueKind <> JsonValueKind.Array then refuse MalformedSyntax (name + " must be an array")
        items.EnumerateArray() |> Seq.toList
    let private step value =
        fields ["run"; "echo"; "archive"; "test_report"; "timeout_seconds"; "env"; "working_directory"; "always"] value
        let operations = ["run"; "echo"; "archive"; "test_report"] |> List.choose (fun name -> optional name value |> Option.map (fun v -> name, text v))
        let operation =
            match operations with
            | ["run", command] when not (String.IsNullOrWhiteSpace command) -> Run command
            | ["echo", message] -> Echo message
            | ["archive", patterns] when not (String.IsNullOrWhiteSpace patterns) -> Archive patterns
            | ["test_report", patterns] when not (String.IsNullOrWhiteSpace patterns) -> TestReport patterns
            | _ -> refuse ExpectedSteps "each step requires exactly one nonempty operation (run, echo, archive, test_report)"
        let timeout =
            match optional "timeout_seconds" value with
            | None -> 600
            | Some raw ->
                match raw.TryGetInt32() with
                | true, seconds when seconds >= 1 && seconds <= 86400 -> seconds
                | _ -> refuse MalformedSyntax "timeout_seconds must be an integer from 1 to 86400"
        let directory = optional "working_directory" value |> Option.map text |> Option.defaultValue "."
        if String.IsNullOrWhiteSpace directory || IO.Path.IsPathRooted directory || directory.Contains '\\'
           || directory.Split('/') |> Array.exists (fun part -> part = ".." || part = "") then
            refuse MalformedSyntax "working_directory must stay beneath the workspace"
        let always =
            match optional "always" value with
            | None -> false
            | Some raw when raw.ValueKind = JsonValueKind.True -> true
            | Some raw when raw.ValueKind = JsonValueKind.False -> false
            | _ -> refuse MalformedSyntax "always must be a boolean"
        { Operation = operation; TimeoutSeconds = timeout; Environment = environment value
          WorkingDirectory = directory; Always = always }
    let parseWithLimits (limits: Limits) (source: string) : Result<Pipeline, AdmissionError> =
        try
            if String.IsNullOrWhiteSpace source then refuse EmptySource "pipeline is empty"
            if Encoding.UTF8.GetByteCount source > limits.MaxSourceBytes then refuse SourceTooLarge "pipeline exceeds source byte limit"
            use document = JsonDocument.Parse(source, JsonDocumentOptions(MaxDepth = limits.MaxDepth))
            let mutable nodes = 0
            let rec check (value: JsonElement) =
                nodes <- nodes + 1
                if nodes > limits.MaxNodes then refuse TooManyNodes "pipeline exceeds node limit"
                match value.ValueKind with
                | JsonValueKind.String when Encoding.UTF8.GetByteCount(value.GetString()) > limits.MaxScalarBytes ->
                    refuse ScalarTooLong "pipeline scalar exceeds byte limit"
                | JsonValueKind.Array ->
                    if value.GetArrayLength() > limits.MaxCollectionItems then refuse TooManyCollectionItems "array exceeds item limit"
                    for child in value.EnumerateArray() do check child
                | JsonValueKind.Object ->
                    let properties = value.EnumerateObject() |> Seq.toArray
                    if properties.Length > limits.MaxCollectionItems then refuse TooManyCollectionItems "object exceeds item limit"
                    for property in properties do
                        if Encoding.UTF8.GetByteCount property.Name > limits.MaxScalarBytes then refuse ScalarTooLong "field name exceeds byte limit"
                        check property.Value
                | _ -> ()
            check document.RootElement
            let root = document.RootElement
            fields ["version"; "env"; "stages"] root
            let version = required "version" root
            if version.ValueKind <> JsonValueKind.Number || version.GetRawText() <> "1" then
                refuse UnsupportedConstruct "unsupported pipeline version; expected 1"
            let names = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            let stages = array "stages" root |> List.map (fun stage ->
                fields ["name"; "steps"] stage
                let name = required "name" stage |> text
                if String.IsNullOrWhiteSpace name || name.Length > 128 || name |> Seq.exists Char.IsControl then
                    refuse ExpectedStage "stage name must be 1–128 characters without control characters"
                if not (names.Add name) then refuse DuplicateSection "duplicate stage name"
                let steps = array "steps" stage |> List.map step
                if steps.IsEmpty then refuse ExpectedSteps "stage requires at least one step"
                { Name = name; Steps = steps })
            if stages.IsEmpty then refuse NoStages "pipeline requires at least one stage"
            Ok { Version = 1; Environment = environment root; Stages = stages }
        with
        | Invalid(code, message) -> Error(AdmissionError.at code 1L 1L message)
        | :? JsonException as error ->
            Error(AdmissionError.at MalformedSyntax (error.LineNumber.GetValueOrDefault() + 1L)
                      (error.BytePositionInLine.GetValueOrDefault() + 1L) "invalid pipeline JSON")
        | :? InvalidOperationException -> Error(AdmissionError.at MalformedSyntax 1L 1L "invalid field type")
    let parse source = parseWithLimits Limits.defaults source
