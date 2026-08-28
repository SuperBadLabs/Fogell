#load "prelude.fsx"
/// FG-052. Push every SCM-marked case (first line `//// SCM JOB ////`) to the
/// fixture repo as branch case/<stem> with the body at /Jenkinsfile — the SAME
/// bytes both engines consume. IDEMPOTENT for real: the remote branch's current
/// Jenkinsfile is compared to the desired body and nothing is pushed when they
/// already agree; when content DID change, the commit is stamped with fixed
/// dates so the sha is a function of content+parent, not of when the sync ran —
/// sealed receipts embed the sha and must not churn on rerun.
///
/// Ported from `sync-scm-cases.bb` under FG-226. The babashka original walked
/// `fs/glob` in filesystem order; this walks the same set ordinally sorted, so
/// the branches are visited in a stable order across hosts. That changes only
/// the order of the `synced` lines, never which branches move.
open System
open System.IO
open Prelude

let url =
    match Environment.GetEnvironmentVariable "FOGELL_SCM_URL" with
    | null | "" -> "git://100.105.179.51/repo.git"
    | v -> v

let marker = "//// SCM JOB ////"

type Case = { Stem: string; Body: string }

let scmCases =
    glob "differential/cases" "*.Jenkinsfile"
    |> List.choose (fun path ->
        let content = slurp path
        if not (content.StartsWith(marker, StringComparison.Ordinal)) then None
        else
            let name = Path.GetFileName path
            let stem = (javaRx "\\.Jenkinsfile$").Replace(name, "")
            match content.IndexOf '\n' with
            | -1 ->
                out ("ERROR: " + name + " is marker-only (no body)")
                exitWith 1
            | i -> Some { Stem = stem; Body = content.Substring(i + 1) })

/// Fails closed, mirroring the babashka original where `shell` threw.
let gitIn (work: string) (args: string list) =
    runOrDie ("git " + String.Join(" ", args)) work [] "git" args

/// The two sites the original marked `:continue true` — a missing branch and a
/// missing parent are ordinary answers here, not failures.
let gitTry (work: string) (args: string list) = runIn work [] "git" args

[<EntryPoint>]
let main _ =
    if not (List.isEmpty scmCases) then
        let work = Path.Combine(Path.GetTempPath(), "fogell-scm-sync" + Guid.NewGuid().ToString("N").Substring(0, 12))
        Directory.CreateDirectory work |> ignore
        // `babashka.fs/create-temp-dir` yields rwx------; `Directory.CreateDirectory`
        // yields rwxrwxr-x under umask 002, so the fixture clone was
        // group-readable where it had been private. Restored explicitly.
        File.SetUnixFileMode(work, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        gitIn work [ "clone"; "-q"; url; "." ] |> ignore
        for c in scmCases do
            let branch = "case/" + c.Stem
            let current =
                let r = gitTry work [ "show"; "origin/" + branch + ":Jenkinsfile" ]
                if runOk r then Some r.Out else None
            let mainHead = javaTrim (gitIn work [ "rev-parse"; "origin/main" ]).Out
            let branchParent =
                let r = gitTry work [ "rev-parse"; "origin/" + branch + "^" ]
                if runOk r then Some(javaTrim r.Out) else None
            // Already in agreement — nothing moves, the sealed sha stays put.
            if current = Some c.Body && branchParent = Some mainHead then ()
            else
                gitIn work [ "checkout"; "-q"; "-B"; branch; "origin/main" ] |> ignore
                spit (Path.Combine(work, "Jenkinsfile")) c.Body
                gitIn work [ "add"; "Jenkinsfile" ] |> ignore
                runOrDie ("git commit for " + branch) work
                    [ "GIT_AUTHOR_DATE", "2026-01-01T00:00:00Z"
                      "GIT_COMMITTER_DATE", "2026-01-01T00:00:00Z" ]
                    "git"
                    [ "-c"; "user.email=harness@fogell"; "-c"; "user.name=fogell-harness"
                      "commit"; "-qm"; "sync: " + c.Stem ]
                |> ignore
                gitIn work [ "push"; "-qf"; "origin"; branch ] |> ignore
                out ("synced " + branch)
        Directory.Delete(work, true)
    0
