#I "/home/srikanth/projects/fogell-worktrees/fg-015b/src/Fogell.Differential/bin/Release/net10.0"
#r "Fogell.Differential.dll"

open System
open System.IO
open Fogell.Differential

let cfg =
    { BaseUrl = "http://127.0.0.1:18099"
      CoreVersion = "2.568.1"
      WorkspaceRoot = None
      WorkspaceCollector = None
      DeclaresTimestamps = false }

for path in fsi.CommandLineArgs |> Array.skip 1 do
    let stem = Path.GetFileNameWithoutExtension path
    let job = "probe-" + stem.ToLowerInvariant().Replace("_", "-")
    printfn "===== %s" stem

    match Jenkins.run cfg [] job (File.ReadAllText path) with
    | Error why -> printfn "HARNESS-ERROR: %s" why
    | Ok trace ->
        printfn "RESULT: %s" trace.Result
        for line in trace.Output do
            printfn "| %s" line
