namespace Fogell.Differential

open System
open System.Net.Http
open System.Text
open System.Text.RegularExpressions

/// The Jenkins side of the differential. Drives a PINNED Jenkins over its REST
/// API, runs one Jenkinsfile, and reduces the run to a [Trace].
///
/// Pinning matters: a compatibility claim is against a specific Jenkins, not
/// "Jenkins" in the abstract. The image digest and core version are recorded in
/// every receipt.
type JenkinsConfig =
    { BaseUrl: string
      /// Recorded in the receipt so the claim names what it was measured against.
      CoreVersion: string
      /// A directory visible to this process, when Jenkins shares a filesystem.
      WorkspaceRoot: string option
      /// FG-002b. Jenkins usually does NOT share a filesystem — it runs in a
      /// container, often on another host. Rather than give up on comparing
      /// workspaces (which would cap every receipt at PROVEN-PARTIAL forever),
      /// the harness can be handed a command that hashes the workspace WHERE IT
      /// LIVES and prints `<sha256>  <relative-path>` lines on stdout.
      ///
      /// `{job}` is substituted with the job name. Example:
      ///   ssh luigi 'podman exec jenkins-lab sh -c "cd /var/jenkins_home/workspace/{job} && find . -type f | sort | xargs -r sha256sum"'
      ///
      /// The output is normalised through exactly the same exclusion rules as a
      /// local hash, so neither side gets a different definition of "workspace".
      WorkspaceCollector: string option }

module Jenkins =

    let private client = new HttpClient(Timeout = TimeSpan.FromMinutes 10.0)

    let private crumb (cfg: JenkinsConfig) =
        task {
            let! body = client.GetStringAsync $"{cfg.BaseUrl}/crumbIssuer/api/json"
            let field = Regex.Match(body, "\"crumbRequestField\":\"([^\"]+)\"").Groups[1].Value
            let value = Regex.Match(body, "\"crumb\":\"([^\"]+)\"").Groups[1].Value
            return field, value
        }
        |> fun t -> t.Result

    let private xmlEscape (s: string) =
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

    let private jobXml (script: string) =
        "<flow-definition plugin=\"workflow-job\"><description/><keepDependencies>false</keepDependencies>"
        + "<properties>"
        // PERFORMANCE_OPTIMIZED deliberately: the differential compares SEMANTICS,
        // and MAX_SURVIVABILITY costs ~6.9 fsyncs per step without changing any
        // observable output. Durability is compared separately, not here.
        + "<org.jenkinsci.plugins.workflow.job.properties.DurabilityHintJobProperty>"
        + "<hint>PERFORMANCE_OPTIMIZED</hint>"
        + "</org.jenkinsci.plugins.workflow.job.properties.DurabilityHintJobProperty>"
        + "</properties>"
        + "<definition class=\"org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition\" plugin=\"workflow-cps\">"
        + $"<script>{xmlEscape script}</script><sandbox>true</sandbox></definition>"
        + "<triggers/><disabled>false</disabled></flow-definition>"

    /// FG-110. Run a SEQUENCE of builds of ONE job and return a trace per
    /// build. The job is created once, its definition UPDATED between builds
    /// (a sequence's scripts may differ), and deleted only at the end, so
    /// build history exists and `changed`/`fixed`/`regression` can select
    /// against a real previous result. Each build is polled BY NUMBER — build
    /// k of the sequence is build #k of the job — and the workspace is hashed
    /// after each build, exactly where it lives.
    let runMany
        (cfg: JenkinsConfig)
        (envReplacements: (string * string) list)
        (jobName: string)
        (scripts: string list)
        : Result<Trace, string> list =
        try
            let field, value = crumb cfg

            let post (path: string) (content: HttpContent option) =
                let req = new HttpRequestMessage(HttpMethod.Post, $"{cfg.BaseUrl}{path}")
                req.Headers.Add(field, value)
                content |> Option.iter (fun c -> req.Content <- c)
                let r = client.Send req
                int r.StatusCode

            post $"/job/{jobName}/doDelete" None |> ignore

            let runOneInner (buildNumber: int) (script: string) : Result<Trace, string> =
                let xml () =
                    new StringContent(jobXml script, Encoding.UTF8, "application/xml")

                let ready =
                    if buildNumber = 1 then
                        let created = post $"/createItem?name={jobName}" (Some(xml ()))

                        if created = 200 || created = 201 then
                            Ok()
                        else
                            Error $"createItem returned HTTP {created}"
                    else
                        // update the definition in place; history survives
                        let updated = post $"/job/{jobName}/config.xml" (Some(xml ()))
                        if updated = 200 then Ok() else Error $"config.xml update returned HTTP {updated}"

                match ready with
                | Error e -> Error e
                | Ok() ->

                // FG-103: the trigger's status propagates — a stale crumb or a 409
                // otherwise means five minutes of blind polling for a build that
                // never exists, blamed on "did not reach a terminal state".
                match post $"/job/{jobName}/build" None with
                | 200
                | 201 -> ()
                | other -> failwith $"build trigger returned HTTP {other}"

                // poll THIS build number to a terminal state
                let mutable result = None
                let mutable attempts = 0

                while result.IsNone && attempts < 600 do
                    Threading.Thread.Sleep 500
                    attempts <- attempts + 1

                    try
                        let body =
                            client.GetStringAsync($"{cfg.BaseUrl}/job/{jobName}/{buildNumber}/api/json").Result

                        if Regex.IsMatch(body, "\"building\":false") then
                            let m = Regex.Match(body, "\"result\":\"([A-Z_]+)\"")
                            if m.Success then result <- Some(m.Groups[1].Value.ToLowerInvariant())
                    with _ ->
                        ()

                match result with
                | None -> Error "jenkins build did not reach a terminal state"
                | Some terminal ->
                    let console =
                        client.GetStringAsync($"{cfg.BaseUrl}/job/{jobName}/{buildNumber}/consoleText").Result

                    let workspaceHash, files =
                        match cfg.WorkspaceRoot, cfg.WorkspaceCollector with
                        | Some root, _ -> Trace.hashWorkspace (IO.Path.Combine(root, jobName))
                        | None, Some template -> Trace.collectRemote (template.Replace("{job}", jobName))
                        | None, None -> "not-collected", []

                    let rawLines = console.Replace("\r\n", "\n").Split '\n'

                    let trace =
                        { Result = terminal
                          EngineNotes = []
                          // the workspace root is READ from the run's own banner —
                          // `Running on <node> in <path>` — so a non-default
                          // JENKINS_HOME or a remote agent canonicalises correctly;
                          // the pinned controller path is only the fallback
                          Output =
                            (let fromBanner =
                                rawLines
                                |> Array.tryPick (fun l ->
                                    let m = Text.RegularExpressions.Regex.Match(l.Trim(), "^Running on .+ in (/.+)$")
                                    if m.Success then Some m.Groups[1].Value else None)

                             let ws = defaultArg fromBanner $"/var/jenkins_home/workspace/{jobName}"

                             Trace.normaliseOutputShaped true [ ws, "${WORKSPACE}" ] envReplacements rawLines)
                          WorkspaceHash = workspaceHash
                          WorkspaceFiles = files
                          // Jenkins does not tell us whether the script had a
                          // parallel block; the side that parses does.
                          Concurrent = false
                          ReportedFailureReason = Trace.reportedFailureReason rawLines }

                    Ok trace

            // TOTAL per build: a throw while collecting build k (console fetch,
            // remote workspace collector) is build k's OWN error — it must not
            // reach the outer handler and replace builds 1..k-1's already-collected
            // evidence with a misattributed message.
            let runOne (buildNumber: int) (script: string) : Result<Trace, string> =
                try
                    runOneInner buildNumber script
                with ex ->
                    Error ex.Message

            let results =
                scripts
                |> List.fold
                    (fun (acc, halted) script ->
                        if halted then
                            (Error "a prior build in this sequence failed to run" :: acc, true)
                        else
                            let r = runOne (List.length acc + 1) script

                            match r with
                            | Ok _ -> (r :: acc, false)
                            | Error _ -> (r :: acc, true))
                    ([], false)
                |> fun (acc, _) -> List.rev acc

            // Best-effort cleanup AFTER the evidence is safe: a delete failure
            // must not replace collected traces (the next run of this case
            // deletes the job first anyway).
            (try
                post $"/job/{jobName}/doDelete" None |> ignore
             with _ ->
                 ())

            results
        with ex ->
            // one entry PER REQUESTED BUILD, so a caller zipping against the
            // fogell side cannot misalign a sequence on a harness exception
            scripts |> List.map (fun _ -> Error ex.Message)

    /// Run one Jenkinsfile under a disposable job name — the pre-FG-110 contract.
    let run (cfg: JenkinsConfig) (envReplacements: (string * string) list) (jobName: string) (script: string) =
        match runMany cfg envReplacements jobName [ script ] with
        | [ r ] -> r
        | _ -> Error "single-build run returned an unexpected shape"
