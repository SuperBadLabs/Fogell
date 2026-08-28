#load "prelude.fsx"
/// FG-046b. The measurement behind the `input` approval semantics, kept in the
/// repo so the claim is rerunnable rather than remembered. Requires the pinned
/// lab (Jenkins 2.568.1) reachable at $JENKINS_URL, default http://127.0.0.1:18099.
///
///   scripts/bin/probe-input approve   — what does APPROVING print?
///   scripts/bin/probe-input reject    — what does a human ABORT print?
///   scripts/bin/probe-input restart   — does a PENDING prompt survive a restart?
///
/// Results as measured 2026-08-01 (recorded in docs/adr/0005):
///   approve  -> console goes straight from the prompt to the next step. No
///               "Approved by ..." line. Result SUCCESS.
///   reject   -> `Rejected`, then
///               `org.jenkinsci.plugins.workflow.actions.ErrorAction$ErrorId: <uuid>`.
///               Result ABORTED.
///   restart  -> the pending action keeps the SAME hex id across a controller
///               restart and is still approvable; result SUCCESS.
///
/// The restart mode needs the lab's container restart command, since a controller
/// restart is not something the REST API can be asked for honestly:
///   RESTART_CMD='ssh luigi podman restart jenkins-lab' scripts/bin/probe-input restart
///
/// Ported from `probe-input.bb` under FG-226.
open System
open System.Net.Http
open System.Text
open System.Threading
open Prelude

let baseUrl =
    match Environment.GetEnvironmentVariable "JENKINS_URL" with
    | null | "" -> "http://127.0.0.1:18099"
    | v -> v

/// Cookies are handled BY HAND rather than by a CookieContainer, because the
/// crumb below is bound to the session that issued it and the original carries
/// exactly the cookie pairs from one `Set-Cookie` response, nothing more.
let handler = new HttpClientHandler(UseCookies = false, AllowAutoRedirect = true)
let http = new HttpClient(handler, Timeout = TimeSpan.FromSeconds 30.0)

type Resp = { Status: int; Body: string; SetCookie: string list }

let get (url: string) =
    try
        use req = new HttpRequestMessage(HttpMethod.Get, url)
        use r = http.Send req
        let body = r.Content.ReadAsStringAsync().Result
        let sc =
            match r.Headers.TryGetValues "Set-Cookie" with
            | true, vs -> List.ofSeq vs
            | _ -> []
        { Status = int r.StatusCode; Body = body; SetCookie = sc }
    // A connection REFUSED is not an HTTP status — during a restart the tunnel
    // simply has nothing to talk to, and a non-throwing option does not cover it.
    with _ -> { Status = 0; Body = ""; SetCookie = [] }

let post (url: string) (headers: (string * string) list) (body: string option) =
    try
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        for (k, v) in headers do
            if not (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) then
                req.Headers.TryAddWithoutValidation(k, v) |> ignore
        match body with
        | Some b ->
            let ct =
                headers
                |> List.tryFind (fun (k, _) -> k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                |> Option.map snd
                |> Option.defaultValue "application/xml"
            req.Content <- new StringContent(b, Encoding.UTF8, ct)
        | None -> ()
        use r = http.Send req
        { Status = int r.StatusCode; Body = r.Content.ReadAsStringAsync().Result; SetCookie = [] }
    with _ -> { Status = 0; Body = ""; SetCookie = [] }

let group1 (pattern: string) (s: string) =
    let m = (javaRx pattern).Match s
    if m.Success then Some m.Groups.[1].Value else None

/// TWO GETTERS, AS THE ORIGINAL HAD, and collapsing them was a defect.
/// `probe-input.bb` had a tolerant `try-get` (`:throw false` plus a catch) used
/// ONLY for the restart polling path — its comment says why: "a connection
/// REFUSED is not an HTTP status" while the controller is down. The crumb
/// request was a plain `http/get`, which THROWS. The port gave everything the
/// tolerant getter, so against an unreachable Jenkins `crumbHeaders` returned
/// no session, every later call failed silently, and `restart` mode printed
/// `SAME ID: true` — comparing `None` to `None` — then exited 0. That fabricates
/// the exact measurement this tool exists to make and that ADR 0005 cites.
/// Raised by Codex on PR #181; it is the eighth defect's class, in the HTTP
/// setup path that the process-call audit for that defect never looked at.
let getOrDie (what: string) (url: string) =
    let r = get url
    if r.Status = 0 then
        eout ("FAIL: " + what + ": " + baseUrl + " is not reachable")
        exitWith 1
    if r.Status >= 400 then
        eout ("FAIL: " + what + ": HTTP " + string r.Status)
        exitWith 1
    r

/// The crumb is bound to the SESSION that issued it: without carrying the
/// Set-Cookie back, every POST below is a 403.
let crumbHeaders () =
    let r = getOrDie "crumb request" (baseUrl + "/crumbIssuer/api/json")
    let field = group1 "\"crumbRequestField\":\"([^\"]+)\"" r.Body
    let crumb = group1 "\"crumb\":\"([^\"]+)\"" r.Body
    [ match field, crumb with
      | Some f, Some c -> yield (f, c)
      | _ -> ()
      if not (List.isEmpty r.SetCookie) then
          let jar =
              r.SetCookie
              |> List.map (fun c -> (c.Split(';').[0]))
              |> String.concat "; "
          yield ("Cookie", jar) ]

let script =
    "pipeline {\n  agent any\n  stages {\n    stage('gate') {\n      steps {\n"
    + "        sh 'echo before-gate'\n"
    + "        input message: 'Deploy?', ok: 'Ship it'\n"
    + "        sh 'echo after-approval'\n"
    + "      }\n    }\n  }\n}"

/// `str/escape` with the same three-entry map: `&` must not be rewritten after
/// `<` and `>` introduce their own ampersands, so all three are applied in one
/// pass over the source characters rather than as successive replaces.
let xmlEscape (s: string) =
    let sb = StringBuilder()
    for c in s do
        match c with
        | '<' -> sb.Append "&lt;" |> ignore
        | '>' -> sb.Append "&gt;" |> ignore
        | '&' -> sb.Append "&amp;" |> ignore
        | _ -> sb.Append c |> ignore
    sb.ToString()

let xml =
    "<flow-definition plugin=\"workflow-job\"><description/><keepDependencies>false</keepDependencies><properties/>"
    + "<definition class=\"org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition\" plugin=\"workflow-cps\">"
    + "<script>" + xmlEscape script + "</script><sandbox>true</sandbox></definition>"
    + "<triggers/><disabled>false</disabled></flow-definition>"

/// babashka's `shell` given one string TOKENIZES it rather than handing it to a
/// shell, so no metacharacter is interpreted. Reproduced here, quotes included,
/// so `RESTART_CMD` keeps meaning exactly what it meant.
let tokenize (s: string) =
    let toks = ResizeArray<string>()
    let cur = StringBuilder()
    let mutable quote = '\000'
    for c in s do
        if quote <> '\000' then
            if c = quote then quote <- '\000' else cur.Append c |> ignore
        elif c = '"' || c = '\'' then quote <- c
        elif Char.IsWhiteSpace c then
            if cur.Length > 0 then toks.Add(cur.ToString()); cur.Clear() |> ignore
        else cur.Append c |> ignore
    if cur.Length > 0 then toks.Add(cur.ToString())
    List.ofSeq toks

[<EntryPoint>]
let main argv =
    let mode = if argv.Length > 0 then argv.[0] else "approve"
    let job = "probe-input-" + mode

    let pendingId () =
        let r = get (baseUrl + "/job/" + job + "/1/wfapi/nextPendingInputAction")
        if r.Status = 200 then group1 "\"id\":\"([^\"]+)\"" r.Body else None

    let awaitPending (tries: int) =
        let mutable i = 0
        let mutable found = None
        let mutable go = true
        while go do
            Thread.Sleep 1000
            found <- pendingId ()
            if found.IsSome || i >= tries then go <- false else i <- i + 1
        found

    let hdrs = crumbHeaders ()
    post (baseUrl + "/job/" + job + "/doDelete") hdrs None |> ignore
    post (baseUrl + "/createItem?name=" + job) (("Content-Type", "application/xml") :: hdrs) (Some xml) |> ignore
    post (baseUrl + "/job/" + job + "/build") hdrs None |> ignore

    let id = awaitPending 60
    out ("pending input id: " + (match id with Some v -> v | None -> ""))

    if mode = "restart" then
        let cmd =
            match Environment.GetEnvironmentVariable "RESTART_CMD" with
            | null | "" -> "ssh luigi podman restart jenkins-lab"
            | v -> v
        out ("restarting the controller: " + cmd)
        match tokenize cmd with
        | exe :: args -> runOrDie ("RESTART_CMD: " + cmd) "" [] exe args |> ignore
        | [] -> ()
        let mutable i = 0
        let mutable serving = false
        while not serving && i <= 100 do
            Thread.Sleep 3000
            if (get (baseUrl + "/api/json")).Status = 200 then
                out ("controller serving again after ~ " + string (3 * (i + 1)) + " s")
                serving <- true
            else i <- i + 1
        let id2 = awaitPending 30
        out ("pending id AFTER restart: " + (match id2 with Some v -> v | None -> ""))
        out ("SAME ID: " + (if id2 = id then "true" else "false"))

    // The crumb is re-issued: a restarted controller does not know the old session.
    let id = match awaitPending 30 with Some v -> Some v | None -> id
    let hdrs2 = crumbHeaders ()
    let path = if mode = "reject" then "abort" else "proceedEmpty"
    match id with
    | Some idv ->
        let r = post (baseUrl + "/job/" + job + "/1/input/" + idv + "/" + path) hdrs2 None
        out (path + " -> " + string r.Status)
    | None -> ()

    let mutable i = 0
    let mutable settled = false
    while not settled do
        Thread.Sleep 2000
        let r = get (baseUrl + "/job/" + job + "/1/api/json")
        if r.Status = 200 && (javaRx "\"building\":false").IsMatch r.Body then
            out ("result: " + (match group1 "\"result\":\"([A-Z_]+)\"" r.Body with Some v -> v | None -> ""))
            settled <- true
        elif i < 60 then i <- i + 1
        else
            out "TIMEOUT waiting for the build to finish"
            settled <- true

    out "---console---"
    out (get (baseUrl + "/job/" + job + "/1/consoleText")).Body
    post (baseUrl + "/job/" + job + "/doDelete") (crumbHeaders ()) None |> ignore
    0
