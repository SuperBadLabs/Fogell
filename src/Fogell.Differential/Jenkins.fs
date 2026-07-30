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
      /// A directory visible to BOTH engines, so the workspace can be hashed.
      /// Jenkins runs in a container, so this is the host path of its workspace.
      WorkspaceRoot: string option }

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

    /// Run one Jenkinsfile under a disposable job name and return its trace.
    let run (cfg: JenkinsConfig) (jobName: string) (script: string) : Result<Trace, string> =
        try
            let field, value = crumb cfg

            let post (path: string) (content: HttpContent option) =
                let req = new HttpRequestMessage(HttpMethod.Post, $"{cfg.BaseUrl}{path}")
                req.Headers.Add(field, value)
                content |> Option.iter (fun c -> req.Content <- c)
                let r = client.Send req
                int r.StatusCode

            post $"/job/{jobName}/doDelete" None |> ignore

            let xml = new StringContent(jobXml script, Encoding.UTF8, "application/xml")
            let created = post $"/createItem?name={jobName}" (Some xml)

            if created <> 200 && created <> 201 then
                Error $"createItem returned HTTP {created}"
            else
                post $"/job/{jobName}/build" None |> ignore

                // poll to a terminal state
                let mutable result = None
                let mutable attempts = 0

                while result.IsNone && attempts < 600 do
                    Threading.Thread.Sleep 500
                    attempts <- attempts + 1

                    try
                        let body =
                            client.GetStringAsync($"{cfg.BaseUrl}/job/{jobName}/lastBuild/api/json").Result

                        if Regex.IsMatch(body, "\"building\":false") then
                            let m = Regex.Match(body, "\"result\":\"([A-Z_]+)\"")
                            if m.Success then result <- Some(m.Groups[1].Value.ToLowerInvariant())
                    with _ ->
                        ()

                match result with
                | None -> Error "jenkins build did not reach a terminal state"
                | Some terminal ->
                    let console =
                        client.GetStringAsync($"{cfg.BaseUrl}/job/{jobName}/lastBuild/consoleText").Result

                    let workspaceHash, files =
                        match cfg.WorkspaceRoot with
                        | Some root -> Trace.hashWorkspace (IO.Path.Combine(root, jobName))
                        | None -> "not-collected", []

                    let trace =
                        { Result = terminal
                          Output = Trace.normaliseOutput (console.Replace("\r\n", "\n").Split '\n')
                          WorkspaceHash = workspaceHash
                          WorkspaceFiles = files }

                    post $"/job/{jobName}/doDelete" None |> ignore
                    Ok trace
        with ex ->
            Error ex.Message
