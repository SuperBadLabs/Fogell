module Fogell.Differential.Cli.Program

open System
open System.IO
open Fogell.Differential

/// FG-002. Runs one or more Jenkinsfiles through BOTH engines and seals a
/// receipt per file.
///
/// Usage:
///   fogell-diff <jenkins-url> <jenkins-core> <receipt-dir> <file.Jenkinsfile>...
///
/// Environment:
///   FOGELL_JENKINS_WORKSPACE  host path of Jenkins' workspace root (optional;
///                             without it, workspace hashes are not compared)
[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | baseUrl :: core :: receiptDir :: (_ :: _ as files) ->
        let jenkinsWorkspace =
            match Environment.GetEnvironmentVariable "FOGELL_JENKINS_WORKSPACE" with
            | null | "" -> None
            | v -> Some v

        let collector =
            match Environment.GetEnvironmentVariable "FOGELL_JENKINS_WORKSPACE_CMD" with
            | null | "" -> None
            | v -> Some v

        let envCollector =
            match Environment.GetEnvironmentVariable "FOGELL_JENKINS_ENV_CMD" with
            | null | "" -> None
            | v -> Some v

        let cfg =
            { BaseUrl = baseUrl
              CoreVersion = core
              WorkspaceRoot = jenkinsWorkspace
              WorkspaceCollector = collector
              EnvCollector = envCollector }

        let fogellRoot =
            Path.Combine(Path.GetTempPath(), "fogell-diff-" + Guid.NewGuid().ToString("N").Substring(0, 8))

        Directory.CreateDirectory fogellRoot |> ignore

        printfn "jenkins:   %s (core %s)" baseUrl core
        printfn
            "workspace: %s"
            (match jenkinsWorkspace, collector with
             | Some p, _ -> $"local {p}"
             | None, Some c -> $"remote collector: {c.Substring(0, min 60 c.Length)}..."
             | None, None -> "<not collected — workspace hashes NOT compared>")
        printfn ""

        let receipts =
            files
            |> List.mapi (fun i file ->
                let name = Path.GetFileName file
                let script = File.ReadAllText file
                // Job names MUST be derived from the file, not its index: an
                // index-derived name reshuffles when a case is added, and the
                // new occupant inherits the previous run's workspace. That
                // showed up as a phantom workspace-hash divergence.
                let job =
                    "diff-"
                    + Text.RegularExpressions.Regex.Replace(
                        Path.GetFileNameWithoutExtension name, "[^A-Za-z0-9]+", "-")

                // wipe any stale workspace so a hash can only match on merit
                match Environment.GetEnvironmentVariable "FOGELL_JENKINS_WIPE_CMD" with
                | null
                | "" -> ()
                | template ->
                    let psi = Diagnostics.ProcessStartInfo("/bin/sh")
                    psi.ArgumentList.Add "-c"
                    psi.ArgumentList.Add(template.Replace("{job}", job))
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError <- true
                    use p = Diagnostics.Process.Start psi
                    p.WaitForExit 30_000 |> ignore

                let jenkins = Jenkins.run cfg job script
                let fogell = FogellSide.run fogellRoot job script
                let r = Compare.receipt name core jenkins fogell
                let path = Compare.seal receiptDir r

                let workspaceCompared =
                    match r.Jenkins, r.Fogell with
                    | Some j, Some f -> Compare.workspaceWasCompared j f
                    | _ -> false

                let verdict =
                    match r.Verdict with
                    | Proven when workspaceCompared -> "PROVEN"
                    | Proven -> "PROVEN-PARTIAL"
                    | Diverged ds -> $"DIVERGED({ds.Length})"
                    | NotComparable _ -> "NOT-COMPARABLE"

                printfn "  %-46s %-16s %s" (name.Substring(0, min 44 name.Length)) verdict (Path.GetFileName path)

                match r.Verdict with
                | Diverged ds -> for d in ds do printfn "        %s" d.Describe
                | NotComparable d -> printfn "        %s" d.Describe
                | Proven -> ()

                r)

        let isFull (r: Receipt) =
            r.Verdict = Proven
            && (match r.Jenkins, r.Fogell with
                | Some j, Some f -> Compare.workspaceWasCompared j f
                | _ -> false)

        let full = receipts |> List.filter isFull |> List.length
        let partial = receipts |> List.filter (fun r -> r.Verdict = Proven && not (isFull r)) |> List.length

        printfn ""
        printfn "tier-1 proven (incl. workspace): %d / %d" full receipts.Length
        printfn "proven-partial (result+output):  %d / %d" partial receipts.Length

        // Exit non-zero unless every file is fully proven. A partial pass is not
        // a pass: it is a claim with a hole in it.
        if full = receipts.Length then 0 else 1
    | _ ->
        eprintfn "usage: fogell-diff <jenkins-url> <jenkins-core> <receipt-dir> <file...>"
        2
