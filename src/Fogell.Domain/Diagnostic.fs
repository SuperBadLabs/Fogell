namespace Fogell.Domain

open System
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

/// Versioned, bounded evidence, never instructions to a consuming agent.
[<CLIMutable>]
type ExecutionDiagnostic =
    { [<JsonPropertyName "schema_version">] SchemaVersion: int
      [<JsonPropertyName "category">] Category: string
      [<JsonPropertyName "stage">] Stage: string
      [<JsonPropertyName "step">] Step: string
      [<JsonPropertyName "result">] ResultCode: string
      [<JsonPropertyName "exit_code">] ExitCode: Nullable<int>
      [<JsonPropertyName "test_name">] TestName: string
      [<JsonPropertyName "test_class">] TestClass: string
      [<JsonPropertyName "message">] Message: string
      [<JsonPropertyName "output">] RelevantOutput: string
      [<JsonPropertyName "source_path">] SourcePath: string
      [<JsonPropertyName "source_line">] SourceLine: Nullable<int64>
      [<JsonPropertyName "source_column">] SourceColumn: Nullable<int64>
      [<JsonPropertyName "report_path">] ReportPath: string
      [<JsonPropertyName "artifact_refs">] ArtifactRefs: string array
      [<JsonPropertyName "truncated">] Truncated: bool }

module ExecutionDiagnostic =
    let create category result message =
        { SchemaVersion = 1; Category = category; Stage = null; Step = null
          ResultCode = result; ExitCode = Nullable(); TestName = null; TestClass = null
          Message = message; RelevantOutput = null; SourcePath = null; SourceLine = Nullable(); SourceColumn = Nullable()
          ReportPath = null; ArtifactRefs = [||]; Truncated = false }

    /// Mask entire fields before clipping so a clipped secret cannot evade masking.
    let sanitize (mask: string -> string) (value: ExecutionDiagnostic) =
        let mutable truncated = value.Truncated
        // Escaping can expand a UTF-8 control byte to six JSON bytes. Reserve
        // a shared 2,000-byte field budget below the 16 KiB encoded frame cap.
        let mutable remaining = 2000
        let clip size text =
            if isNull text then null
            else
                let size = min size remaining
                let safe = mask text
                let mutable chars, bytes = 0, 0
                for rune in safe.EnumerateRunes() do
                    if chars < safe.Length && bytes + rune.Utf8SequenceLength <= size then
                        chars <- chars + rune.Utf16SequenceLength
                        bytes <- bytes + rune.Utf8SequenceLength
                    else bytes <- size + 1
                remaining <- remaining - min size bytes
                if chars < safe.Length then truncated <- true
                safe.Substring(0, chars)
        let result =
            { value with Stage = clip 256 value.Stage; Step = clip 128 value.Step
                         TestName = clip 512 value.TestName; TestClass = clip 512 value.TestClass
                         Message = clip 1024 value.Message; RelevantOutput = clip 1024 value.RelevantOutput; SourcePath = clip 512 value.SourcePath
                         ReportPath = clip 512 value.ReportPath
                         ArtifactRefs = value.ArtifactRefs |> Array.truncate 8 |> Array.map (clip 256) }
        { result with Truncated = truncated || value.ArtifactRefs.Length > 8 }

    let serialize value = JsonSerializer.Serialize<ExecutionDiagnostic> value

    let decode (json: string) =
        try
            let d = JsonSerializer.Deserialize<ExecutionDiagnostic> json
            if isNull (box d) || d.SchemaVersion <> 1
               || not (List.contains d.Category [ "workload_step"; "test"; "infrastructure" ])
               || isNull d.Message || isNull d.ResultCode || isNull d.ArtifactRefs
               || Encoding.UTF8.GetByteCount json > 16384 then None
            else Some d
        with _ -> None
