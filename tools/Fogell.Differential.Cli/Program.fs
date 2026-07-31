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

        // ONE coordinated canonicalisation set for BOTH traces: the union of each
        // engine's inherited values for the curated names, so a literal equal to
        // either engine's value rewrites identically on both sides.
        let jenkinsEnv =
            match Environment.GetEnvironmentVariable "FOGELL_JENKINS_ENV_CMD" with
            | null
            | "" -> []
            | cmd ->
                // FAIL CLOSED (FG-103): a CONFIGURED collector that cannot deliver
                // is a broken harness, and continuing with a partial replacement set
                // would manufacture divergences with no indication why. Refuse the
                // whole run instead.
                let refuse (why: string) =
                    eprintfn $"env collector failed: {why}; refusing to run with a partial canonicalisation set"
                    exit 2

                try
                    let psi = Diagnostics.ProcessStartInfo("/bin/sh")
                    psi.ArgumentList.Add "-c"
                    psi.ArgumentList.Add cmd
                    psi.RedirectStandardOutput <- true
                    psi.UseShellExecute <- false
                    use p = Diagnostics.Process.Start psi
                    // bounded: WAIT first, then read — ReadToEnd on a stalled ssh
                    // held the pipe open forever and the 30 s timeout never applied
                    let reader = p.StandardOutput.ReadToEndAsync()

                    if not (p.WaitForExit 30_000) then
                        (try p.Kill(true) with _ -> ())
                        refuse "timed out after 30 s"

                    if p.ExitCode <> 0 then refuse $"exit code {p.ExitCode}"

                    let out = if reader.Wait 5_000 then reader.Result else refuse "output never arrived"; ""

                    let parsed =
                        out.Split '\n'
                        |> Array.choose (fun line ->
                            match line.IndexOf '=' with
                            | i when i > 0 -> Some(line.Substring(0, i), line.Substring(i + 1).Trim())
                            | _ -> None)
                        |> Array.toList

                    if List.isEmpty parsed then refuse "no NAME=VALUE lines in output"
                    parsed
                with
                | :? System.ComponentModel.Win32Exception as e -> refuse e.Message; []
                | :? AggregateException as e -> refuse e.InnerException.Message; []

        // Canonicalisation stays INJECTIVE by canonicalising only names the CASE
        // actually references: both engines share the script text, so `$PATH` in it
        // means every occurrence of either engine's PATH value is an expansion —
        // while a case that merely PRINTS a path literal gets no rewriting and a
        // cross-engine literal collision diverges visibly. (The residual — a script
        // that both references $NAME and prints the other engine's literal value —
        // requires deliberate construction and is accepted, stated here.)
        let replacementsFor (script: string) =
            Fogell.Differential.Trace.canonicalisedEnvNames
            |> List.filter (fun name ->
                // full identifiers only: `$USERNAME` must not enable USER — a
                // substring gate rewrote a different variable's values to `${USER}`
                let bounded pat =
                    Text.RegularExpressions.Regex.IsMatch(script, pat + "([^A-Za-z0-9_]|$)")

                bounded ("\\$" + name)
                // braced forms, operator suffixes included: ${HOME}, ${HOME:-x},
                // ${HOME%/}, … — the boundary is the operator or the brace
                || Text.RegularExpressions.Regex.IsMatch(script, "\\$\\{" + name + "([^A-Za-z0-9_]|\\})")
                // Groovy's spelling of the same reference
                || bounded ("env\\." + name))
            |> List.collect (fun name ->
                [ match jenkinsEnv |> List.tryFind (fun (n, _) -> n = name) with
                  | Some(_, v) when v <> "" -> yield v, "${" + name + "}"
                  | _ -> ()

                  match Environment.GetEnvironmentVariable name with
                  | null
                  | "" -> ()
                  | v -> yield v, "${" + name + "}" ])
            |> List.distinct
            // INJECTIVITY: a raw value claimed by two different names (USER on one
            // engine equals LOGNAME on the other) cannot be canonicalised without
            // erasing which variable it was — swapped values would collapse to one
            // token and falsely prove. Ambiguous values drop out entirely, so any
            // real difference diverges visibly instead.
            |> List.groupBy fst
            |> List.choose (fun (_, pairs) ->
                match pairs |> List.map snd |> List.distinct with
                | [ _ ] -> Some pairs.Head
                | _ -> None)

        let cfg =
            { BaseUrl = baseUrl
              CoreVersion = core
              WorkspaceRoot = jenkinsWorkspace
              WorkspaceCollector = collector }

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

                let caseReplacements = replacementsFor script
                let jenkins = Jenkins.run cfg caseReplacements job script
                let fogell = FogellSide.run caseReplacements fogellRoot job script
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
