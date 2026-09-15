namespace Fogell.Execution

open System
open System.Globalization

/// Controller and runner configuration for bounded artifact publication.
/// The environment is parsed once at controller startup; child runners receive
/// the normalized values explicitly after their inherited environment is cleared.
module ArtifactPolicy =

    [<Literal>]
    let MaxFileBytesVariable = "FOGELL_ARTIFACT_MAX_FILE_BYTES"

    [<Literal>]
    let MaxTotalBytesVariable = "FOGELL_ARTIFACT_MAX_TOTAL_BYTES"

    [<Literal>]
    let MaxFilesVariable = "FOGELL_ARTIFACT_MAX_FILES"

    [<Literal>]
    let MaxScanEntriesVariable = "FOGELL_ARTIFACT_MAX_SCAN_ENTRIES"

    let private parsePositiveInt64 name (raw: string) =
        match Int64.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, value when value > 0L -> Ok value
        | _ -> Error $"{name} must be a positive decimal integer"

    let private parsePositiveInt name (raw: string) =
        match Int32.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, value when value > 0 -> Ok value
        | _ -> Error $"{name} must be a positive decimal integer"

    let private optional getter name defaultValue parse =
        match getter name with
        | null -> Ok defaultValue
        | raw -> parse name raw

    let load (getter: string -> string) : Result<ArtifactLimits, string> =
        let fileBytes = optional getter MaxFileBytesVariable ArtifactLimits.Defaults.MaxFileBytes parsePositiveInt64
        let totalBytes = optional getter MaxTotalBytesVariable ArtifactLimits.Defaults.MaxTotalBytes parsePositiveInt64
        let files = optional getter MaxFilesVariable ArtifactLimits.Defaults.MaxFiles parsePositiveInt
        let scanEntries = optional getter MaxScanEntriesVariable ArtifactLimits.Defaults.MaxScanEntries parsePositiveInt

        let errors =
            [ match fileBytes with
              | Error error -> yield error
              | Ok _ -> ()
              match totalBytes with
              | Error error -> yield error
              | Ok _ -> ()
              match files with
              | Error error -> yield error
              | Ok _ -> ()
              match scanEntries with
              | Error error -> yield error
              | Ok _ -> () ]

        if not (List.isEmpty errors) then
            Error(String.concat "; " errors)
        else
            let value = function
                | Ok item -> item
                | Error _ -> invalidOp "artifact policy parse result was unexpectedly unavailable"

            let fileBytes = value fileBytes
            let totalBytes = value totalBytes
            let files = value files
            let scanEntries = value scanEntries

            if fileBytes > totalBytes then
                Error $"{MaxFileBytesVariable} must be no greater than {MaxTotalBytesVariable}"
            else
                Ok
                    { MaxFileBytes = fileBytes
                      MaxTotalBytes = totalBytes
                      MaxFiles = files
                      MaxScanEntries = scanEntries }

    let loadEnvironment () = load Environment.GetEnvironmentVariable

    let environmentValues (limits: ArtifactLimits) =
        [ MaxFileBytesVariable, limits.MaxFileBytes.ToString(CultureInfo.InvariantCulture)
          MaxTotalBytesVariable, limits.MaxTotalBytes.ToString(CultureInfo.InvariantCulture)
          MaxFilesVariable, limits.MaxFiles.ToString(CultureInfo.InvariantCulture)
          MaxScanEntriesVariable, limits.MaxScanEntries.ToString(CultureInfo.InvariantCulture) ]
