namespace Fogell.Execution

open System.IO

/// Hard ceilings applied while publishing an artifact snapshot.  The limits are
/// deliberately part of the store so callers which share a store cannot
/// accidentally opt into different admission rules for the same build.
type ArtifactLimits =
    { MaxFileBytes: int64
      MaxTotalBytes: int64
      MaxFiles: int
      MaxScanEntries: int }

    static member Defaults =
        { MaxFileBytes = 256L * 1024L * 1024L
          MaxTotalBytes = 1024L * 1024L * 1024L
          MaxFiles = 10_000
          MaxScanEntries = 100_000 }

[<RequireQualifiedAccess>]
type ArtifactLimitReason =
    | FileBytes
    | TotalBytes
    | FileCount
    | ScanEntries

/// A safe, machine-readable failure for artifact admission.  Its message and
/// reason contain no workspace paths or other untrusted input.
type ArtifactLimitExceededException(reason: ArtifactLimitReason) =
    inherit IOException("Artifact publication limit exceeded.")

    member _.Reason = reason
