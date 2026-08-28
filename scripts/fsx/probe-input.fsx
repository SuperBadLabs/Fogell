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

type Resp =
    { Status: int
      Body: string
      SetCookie: string list
      Session: string option
      Error: string option }

let responseSession (r: HttpResponseMessage) =
    match r.Headers.TryGetValues "X-Jenkins-Session" with
    | true, values -> values |> Seq.tryFind (String.IsNullOrWhiteSpace >> not)
    | _ -> None

let getWithin (timeout: TimeSpan) (url: string) =
    try
        use req = new HttpRequestMessage(HttpMethod.Get, url)
        use cancel = new CancellationTokenSource(timeout)
        use r = http.Send(req, cancel.Token)
        let body = r.Content.ReadAsStringAsync().Result
        let sc =
            match r.Headers.TryGetValues "Set-Cookie" with
            | true, vs -> List.ofSeq vs
            | _ -> []
        { Status = int r.StatusCode
          Body = body
          SetCookie = sc
          Session = responseSession r
          Error = None }
    // A connection REFUSED is not an HTTP status — during a restart the tunnel
    // simply has nothing to talk to, and a non-throwing option does not cover it.
    with ex ->
        { Status = 0
          Body = ""
          SetCookie = []
          Session = None
          Error = Some ex.Message }

// The original `try-get` bounded every tolerant polling request at five
// seconds. Keep setup calls on the client's longer timeout, but never multiply
// that 30-second bound across the 101-attempt restart loop.
let get (url: string) = getWithin (TimeSpan.FromSeconds 30.0) url
let tryGet (url: string) = getWithin (TimeSpan.FromSeconds 5.0) url

let post (url: string) (headers: (string * string) list) (body: string option) =
    try
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        for (k, v) in headers do
            if not (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) then
                if not (req.Headers.TryAddWithoutValidation(k, v)) then
                    invalidOp ("invalid HTTP header: " + k)
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
        { Status = int r.StatusCode
          Body = r.Content.ReadAsStringAsync().Result
          SetCookie = []
          Session = responseSession r
          Error = None }
    with ex ->
        { Status = 0
          Body = ""
          SetCookie = []
          Session = None
          Error = Some ex.Message }

let failHttp (what: string) (r: Resp) =
    if r.Status = 0 then
        let detail = defaultArg r.Error (baseUrl + " is not reachable")
        eout ("FAIL: " + what + ": " + detail)
    else
        eout ("FAIL: " + what + ": HTTP " + string r.Status)
    exitWith 1

/// Jenkins setup and action requests are measurement prerequisites, not best
/// effort cleanup. The port originally dropped every POST response, so a 403 or
/// 500 could leave no build running and still produce a zero-exit timeout
/// transcript. Only the initial delete admits 404: absence of the job being
/// replaced is the expected clean-start state.
let postOrDie (what: string) (allowNotFound: bool) (url: string) (headers: (string * string) list) (body: string option) =
    let r = post url headers body
    if r.Status = 0 || r.Status >= 400 && not (allowNotFound && r.Status = 404) then
        failHttp what r
    r

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
    if r.Status = 0 || r.Status >= 400 then failHttp what r
    r

let tryGetOrDie (what: string) (url: string) =
    let r = tryGet url
    if r.Status = 0 || r.Status >= 400 then failHttp what r
    r

/// The crumb is bound to the SESSION that issued it: without carrying the
/// Set-Cookie back, every POST below is a 403.
let crumbHeaders () =
    let r = getOrDie "crumb request" (baseUrl + "/crumbIssuer/api/json")
    let field = group1 "\"crumbRequestField\":\"([^\"]+)\"" r.Body
    let crumb = group1 "\"crumb\":\"([^\"]+)\"" r.Body
    let required =
        match field, crumb with
        | Some f, Some c
            when not (String.IsNullOrWhiteSpace f)
                 && not (String.IsNullOrWhiteSpace c)
                 && (f |> Seq.forall (fun ch -> Char.IsAsciiLetterOrDigit ch || "!#$%&'*+-.^_`|~".Contains ch)) ->
            [ (f, c) ]
        | _ ->
            eout "FAIL: crumb request: response is missing crumbRequestField or crumb"
            exitWith 1
    [ yield! required
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
        let r = tryGet (baseUrl + "/job/" + job + "/1/wfapi/nextPendingInputAction")
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
    postOrDie "delete prior probe job" true (baseUrl + "/job/" + job + "/doDelete") hdrs None |> ignore
    postOrDie "create probe job" false (baseUrl + "/createItem?name=" + job) (("Content-Type", "application/xml") :: hdrs) (Some xml) |> ignore
    postOrDie "start probe build" false (baseUrl + "/job/" + job + "/build") hdrs None |> ignore

    let id =
        match awaitPending 60 with
        | Some value -> value
        | None ->
            eout "FAIL: probe build did not publish a pending input action"
            exitWith 1
    out ("pending input id: " + id)
    let mutable currentId = id

    if mode = "restart" then
        let beforeSession =
            match (getOrDie "controller identity before restart" (baseUrl + "/api/json")).Session with
            | Some value -> value
            | None ->
                eout "FAIL: controller identity before restart: X-Jenkins-Session is missing"
                exitWith 1
        let cmd =
            match Environment.GetEnvironmentVariable "RESTART_CMD" with
            | null | "" -> "ssh luigi podman restart jenkins-lab"
            | v -> v
        out ("restarting the controller: " + cmd)
        match tokenize cmd with
        | exe :: args -> runOrDie ("RESTART_CMD: " + cmd) "" [] exe args |> ignore
        | [] ->
            eout "FAIL: RESTART_CMD contains no command"
            exitWith 1
        let mutable i = 0
        let mutable serving = false
        while not serving && i <= 100 do
            Thread.Sleep 3000
            if (tryGet (baseUrl + "/api/json")).Status = 200 then
                out ("controller serving again after ~ " + string (3 * (i + 1)) + " s")
                serving <- true
            else i <- i + 1
        if not serving then
            eout "FAIL: controller did not resume serving after restart"
            exitWith 1
        let afterSession =
            match (getOrDie "controller identity after restart" (baseUrl + "/api/json")).Session with
            | Some value -> value
            | None ->
                eout "FAIL: controller identity after restart: X-Jenkins-Session is missing"
                exitWith 1
        if afterSession = beforeSession then
            eout "FAIL: controller identity did not change; RESTART_CMD did not prove a restart"
            exitWith 1
        let id2 =
            match awaitPending 30 with
            | Some value -> value
            | None ->
                eout "FAIL: pending input action did not reappear after restart"
                exitWith 1
        out ("pending id AFTER restart: " + id2)
        out ("SAME ID: " + (if id2 = id then "true" else "false"))
        currentId <- id2

    // The crumb is re-issued: a restarted controller does not know the old session.
    let actionId = match awaitPending 30 with Some value -> value | None -> currentId
    let hdrs2 = crumbHeaders ()
    let path = if mode = "reject" then "abort" else "proceedEmpty"
    let action =
        postOrDie
            (path + " pending input")
            false
            (baseUrl + "/job/" + job + "/1/input/" + actionId + "/" + path)
            hdrs2
            None
    out (path + " -> " + string action.Status)

    let mutable i = 0
    let mutable settled = false
    while not settled do
        Thread.Sleep 2000
        let r = tryGet (baseUrl + "/job/" + job + "/1/api/json")
        if r.Status = 200 && (javaRx "\"building\":false").IsMatch r.Body then
            match group1 "\"result\":\"([A-Z_]+)\"" r.Body with
            | Some result -> out ("result: " + result)
            | None ->
                eout "FAIL: completed probe build has no result"
                exitWith 1
            settled <- true
        elif i < 60 then i <- i + 1
        else
            eout "FAIL: timeout waiting for the probe build to finish"
            exitWith 1

    out "---console---"
    out (tryGetOrDie "probe console" (baseUrl + "/job/" + job + "/1/consoleText")).Body
    postOrDie "delete completed probe job" false (baseUrl + "/job/" + job + "/doDelete") (crumbHeaders ()) None |> ignore
    0
