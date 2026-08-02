// FG-046e. An EVENT-ordered record of what the approvals inbox advertised.
//
// Scenario N of the approval lane names a safety property: a prompt killed by
// its timeout is WITHDRAWN before `retry` publishes the next one, so the inbox
// never lists a gate nothing is listening to. It checked that by SAMPLING the
// directory every 0.2s, which cannot see an implementation that publishes the
// successor first and tidies up inside a sample gap. A fixture that can pass
// while the thing it names is broken is the one shape the operating contract
// forbids, and the reviewer was right to refuse the PR that merely wrote the
// hole down.
//
// Why here and not a script: the gate hosts have no `inotifywait`, and a
// BLOCKING gate should not grow a system package for one lane. Babashka would
// have been the house choice for glue, but its native image omits
// java.nio.file.WatchService entirely. .NET's FileSystemWatcher is inotify-
// backed on Linux, dotnet is already the gate's primary dependency, and F# is
// the language everything else here is written in.
//
//   watch  <dir> <log>   record creates/deletes until killed
//   report <log>         replay, print the peak, exit non-zero on a breach
//
// `report` is THREE-valued on purpose: 0 proven-good, 1 proven-bad, 3 CANNOT
// PROVE. Folding 3 into 0 is precisely how the sampler was wrong — a watcher
// that died observes nothing, and nothing is not a peak of zero.
module Fogell.Watch.Inbox.Program

open System
open System.IO
open System.Threading

let private usage () =
    eprintfn "usage: watch-inbox watch <dir> <log> | watch-inbox report <log>"
    2

let private watch (dir: string) (log: string) =
    if not (Directory.Exists dir) then
        eprintfn "watch-inbox: %s is not a directory" dir
        2
    else
        // AutoFlush: the lane kills this process, so anything sitting in a
        // buffer is an event that never happened as far as the proof is
        // concerned.
        use out = new StreamWriter(log, append = true, AutoFlush = true)
        let sync = obj ()
        let emit (kind: string) (name: string) = lock sync (fun () -> out.WriteLine($"%s{kind} %s{name}"))

        use w = new FileSystemWatcher(dir)
        w.NotifyFilter <- NotifyFilters.FileName
        w.IncludeSubdirectories <- false
        w.Created.Add(fun e -> emit "CREATE" e.Name)
        w.Deleted.Add(fun e -> emit "DELETE" e.Name)
        // A rename INTO the directory is how an atomic publish lands, and a
        // rename OUT is a withdrawal; both must count or an inbox that
        // publishes atomically looks like an inbox that never published.
        w.Renamed.Add(fun e ->
            emit "DELETE" e.OldName
            emit "CREATE" e.Name)
        // Buffer overrun means the runtime dropped events. The record is then
        // incomplete and supports no conclusion in either direction, so say so
        // in the log rather than reporting the peak of whatever survived.
        w.Error.Add(fun _ -> lock sync (fun () -> out.WriteLine "OVERFLOW"))
        w.InternalBufferSize <- 64 * 1024
        w.EnableRaisingEvents <- true

        // READY is written only AFTER EnableRaisingEvents. The caller blocks on
        // it before starting the host: a watcher racing the first publish
        // misses the CREATE, and a missed CREATE makes a breach look compliant.
        out.WriteLine "READY"
        Thread.Sleep Timeout.Infinite
        0

let private report (log: string) =
    let lines =
        if File.Exists log then File.ReadAllLines log else [||]
        |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l) && l <> "READY")

    if lines |> Array.exists (fun l -> l = "OVERFLOW") then
        printfn "approval-watch: the runtime dropped events — the record is incomplete, so it proves nothing"
        3
    else
        let steps =
            lines
            |> Array.choose (fun l ->
                match l.Split([| ' ' |], 2) with
                | [| kind; name |] when name.EndsWith ".pending" -> Some(kind, name)
                | _ -> None)

        let creates = steps |> Array.filter (fst >> (=) "CREATE") |> Array.length
        if creates = 0 then
            printfn "approval-watch: no prompt was ever observed being published — the watcher saw nothing, which is not the same as seeing nothing wrong"
            3
        else
            let _, peak =
                steps
                |> Array.fold
                    (fun (live: Set<string>, peak) (kind, name) ->
                        let live = if kind = "CREATE" then live.Add name else live.Remove name
                        live, max peak live.Count)
                    (Set.empty, 0)

            printfn "approval-watch: %d publish event(s), peak %d live prompt(s)" creates peak
            if peak > 1 then
                printfn "the inbox advertised more than one live prompt — a dead gate was listed alongside its successor"
                1
            else
                0

[<EntryPoint>]
let main argv =
    match argv with
    | [| "watch"; dir; log |] -> watch dir log
    | [| "report"; log |] -> report log
    | _ -> usage ()
