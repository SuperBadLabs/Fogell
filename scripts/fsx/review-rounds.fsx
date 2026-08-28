#load "prelude.fsx"
/// FG-002d. List each automated review round for a PR with its reviewed commit and
/// the findings NEW in that round.
///
/// Exists because of a specific failure: on PR #13 I filtered review comments with a
/// `created_at` cutoff in the wrong timezone, so every poll re-showed round 1. I read
/// that as the reviewer re-posting stale findings, said so, and merged over NINE
/// unread findings — two of which let success-only post arms run on a failed build.
///
/// This script has itself been the subject of four review findings, every one of the
/// class it exists to prevent: it parsed only the first page of `--paginate`; it
/// deduplicated on a truncated title; then on a title without its location; and it
/// grouped rounds by wall-clock minute, which merges two reviews submitted in the same
/// minute and splits one review whose comments cross a boundary — misattributing
/// findings to the wrong commit. Rounds are now keyed by `pull_request_review_id`,
/// which is what actually defines a round.
///
///   usage: scripts/bin/review-rounds <pr-number> [owner/repo]
///
/// Ported from `review-rounds.bb` under FG-226. JSON is read with `JsonDocument`
/// rather than a serializer: source-generated or reflection-based deserialization
/// is the AOT trap this whole port exists to stay clear of, and the DOM reader
/// needs no type model at all.
open System
open System.Text.Json
open Prelude

// ---------------------------------------------------------------- json helpers

let str (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | _ -> null

let num (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt64())
    | _ -> None

let obj (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Object -> Some v
    | _ -> None

/// `gh api --paginate` emits ONE JSON VALUE PER PAGE, so the captured stream is a
/// concatenation of arrays rather than a single document. `AllowMultipleValues`
/// is what lets one reader walk all of them; `JsonDocument.Parse` would stop at
/// the first and silently drop every later page — which is review finding one.
let parseConcatenated (json: string) =
    let bytes = Text.Encoding.UTF8.GetBytes json
    let mutable reader =
        Utf8JsonReader(ReadOnlySpan<byte>(bytes), JsonReaderOptions(AllowMultipleValues = true))
    let acc = ResizeArray<JsonElement>()
    while reader.Read() do
        use doc = JsonDocument.ParseValue(&reader)
        let root = doc.RootElement
        if root.ValueKind = JsonValueKind.Array then
            for item in root.EnumerateArray() do acc.Add(item.Clone())
        else acc.Add(root.Clone())
    List.ofSeq acc

// ---------------------------------------------------------------- title extraction

let badgeRx = javaRx "!\\[P\\d Badge\\]\\(https[^)]*\\)"
let markupRx = javaRx "[*#]|<sub>|</sub>"

let fullTitle (c: JsonElement) =
    let body = match str c "body" with | null -> "" | b -> b
    let cleaned = markupRx.Replace(badgeRx.Replace(body, ""), "")
    let first =
        splitLines cleaned
        |> List.filter (fun l -> not (blank l))
        |> List.tryHead
        |> Option.defaultValue ""
    javaTrim first

let lineOf (c: JsonElement) =
    match num c "line" with
    | Some n -> Some n
    | None -> num c "original_line"

/// Identity is path + line + full title: a truncated title collides, and so does a
/// full title repeated in another file.
let identityOf (c: JsonElement) =
    let path = match str c "path" with | null -> "" | p -> p
    let line = match lineOf c with | Some n -> string n | None -> ""
    (path, line, fullTitle c)

let display (c: JsonElement) =
    let t = fullTitle c
    t.Substring(0, min 72 t.Length)

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        out "usage: scripts/bin/review-rounds <pr-number> [owner/repo]"
        exitWith 2
    let pr = argv.[0]
    let repo = if argv.Length > 1 then argv.[1] else "SuperBadLabs/Fogell"

    let gh (path: string) =
        // FAILS CLOSED. A failed `gh api` returns empty stdout, which parses to
        // zero comments and prints "0 comments across 0 review(s)" — read by
        // someone triaging a PR as "nothing to triage". The babashka original
        // aborted here.
        let r = runOrDie ("gh api repos/" + repo + path) "" [] "gh" [ "api"; "repos/" + repo + path; "--paginate" ]
        parseConcatenated r.Out

    let comments = gh ("/pulls/" + pr + "/comments")
    let reviews = gh ("/pulls/" + pr + "/reviews")
    let reviewById =
        reviews
        |> List.choose (fun r -> num r "id" |> Option.map (fun id -> (id, r)))
        |> List.fold (fun (m: Map<int64, JsonElement>) (k, v) -> m.Add(k, v)) Map.empty

    // A ROUND is a review, not a minute.
    let rounds =
        comments
        |> List.groupBy (fun c -> num c "pull_request_review_id")
        |> List.sortWith (fun (rida, csa) (ridb, csb) ->
            let key (rid: int64 option) (cs: JsonElement list) =
                let submitted =
                    rid
                    |> Option.bind reviewById.TryFind
                    |> Option.map (fun r -> str r "submitted_at")
                    |> Option.defaultValue null
                let whenStr =
                    match submitted with
                    | null -> (match cs with | c :: _ -> (match str c "created_at" with | null -> "" | v -> v) | [] -> "")
                    | v -> v
                (whenStr, (match rid with Some v -> string v | None -> ""))
            let (wa, ra) = key rida csa
            let (wb, rb) = key ridb csb
            match String.CompareOrdinal(wa, wb) with
            | 0 -> String.CompareOrdinal(ra, rb)
            | c -> c)

    out (String.Format("PR #{0} — {1} comments across {2} review(s)",
                       pr, List.length comments, List.length rounds))

    let mutable seen = Set.empty<string * string * string>
    let mutable n = 1
    for (rid, cs) in rounds do
        let r = rid |> Option.bind reviewById.TryFind
        let commitId = r |> Option.map (fun x -> match str x "commit_id" with | null -> "" | v -> v) |> Option.defaultValue ""
        let sha = commitId.Substring(0, min 10 commitId.Length)
        let whenStr =
            match r |> Option.map (fun x -> str x "submitted_at") with
            | Some v when not (isNull v) -> v
            | _ -> (match cs with | c :: _ -> (match str c "created_at" with | null -> "" | v -> v) | [] -> "")
        let who =
            r
            |> Option.bind (fun x -> obj x "user")
            |> Option.map (fun u -> match str u "login" with | null -> "?" | v -> v)
            |> Option.defaultValue "?"
        let ids = cs |> List.map identityOf
        let fresh = ids |> List.filter (fun i -> not (seen.Contains i))
        out (String.Format("\nreview {0}  {1}  {2}  commit {3}  ({4} comment(s), {5} NEW)",
                           n, whenStr, who,
                           (if blank sha then "?" else sha),
                           List.length cs, List.length fresh))
        for c in cs do
            let tag = if seen.Contains(identityOf c) then "seen" else "NEW"
            let path = match str c "path" with | null -> "" | p -> p
            let leaf =
                let parts = path.Split('/')
                if parts.Length = 0 then "" else parts.[parts.Length - 1]
            let loc = leaf + ":" + (match lineOf c with | Some l -> string l | None -> "")
            out (String.Format("  {0,-4} {1,-28} {2}", tag, loc, display c))
        for i in ids do seen <- seen.Add i
        n <- n + 1

    out "\nEvery NEW line must be triaged before merging. A review with 0 NEW is the only safe one to skip."
    0
