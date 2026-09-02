#load "prelude.fsx"
/// Prove the gate's lane partition before any lane is trusted to stand for the
/// whole. `scripts/build-and-test.sh` runs every blocking proof in one sequence
/// locally; the hosted workflow runs the same script once per lane, in parallel,
/// with FOGELL_GATE_LANES naming the lane. That is only equivalent to the local
/// gate if (a) every blocking block sits inside exactly one lane, (b) every lane a
/// block names is one the script lists, and (c) every listed lane owns at least
/// one block. A block outside any lane would run in every hosted job (wasteful,
/// not unsafe); a block under a misspelled lane would run in NO hosted job and
/// the aggregate would still go green — the false-green shape this repository
/// audits for everywhere else, so it is checked here rather than assumed.
///
/// Static: the script is parsed, not executed, except for two cheap behavioural
/// arms (`--list-lanes` and the unknown-lane refusal). The parser's own failure
/// modes are proven on mutated copies of the script text before the real script
/// is judged, because a checker nobody has watched fail is a claim.
///
/// WHAT THE PARSER ASSUMES about build-and-test.sh, and refuses on otherwise:
///   - a lane block opens with exactly `if lane_active <lane>; then` on its own
///     line and closes with a `fi` at the same nesting depth; lane blocks do not
///     nest inside each other and have NO `else`/`elif` arm (an else arm would
///     run in every lane but the named one);
///   - every other multi-line `if` opens a plain block that its own `fi` closes,
///     so a `fi` inside a lane block closes the inner `if`, not the lane; a
///     one-line `if …; then …; fi` opens nothing;
///   - an invocation anywhere on an `if`, `elif` or `fi` line is judged against
///     the block that encloses THAT LINE (the condition of an `if`, the arm of
///     an `elif`, whatever follows a `fi;`), so `if ! ./scripts/x.sh; then`
///     outside every lane is a defect like any other;
///   - helper bodies (`name() {` ... `}` at column 0) are definitions, judged
///     where they are CALLED, not where they are written;
///   - an invocation is any non-comment line that mentions `scripts/` (with or
///     without `./`, in a substitution, behind an env prefix or `timeout`),
///     runs `dotnet run|test|build|msbuild`, or calls `ensure_audit_tools`. A
///     gate step spelled another way is invisible to this audit — a wrapper
///     helper whose body holds the `./scripts/` call (bodies are skipped), a
///     backtick substitution, or a built artifact run by its bin/ path — so
///     keep to those spellings or extend the pattern here, in one place.
///
/// usage: scripts/bin/audit-gate-lanes [path/to/build-and-test.sh]
///   Run from the repository root; the default path is scripts/build-and-test.sh.
open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open Prelude

let invocationRx =
    Regex(@"(^|[\s""'(=/])scripts/[A-Za-z0-9_.-]|\bdotnet (run|test|build|msbuild)\b|\bensure_audit_tools\b")
let laneOpenRx = Regex(@"^\s*if lane_active ([^;\s]+); then\s*$")
let anyIfRx = Regex(@"^\s*if\b")
let oneLineIfRx = Regex(@"^\s*if\b.*;\s*then\b.*\bfi\s*$")
let elseRx = Regex(@"^\s*(else|elif)\b")
let fiRx = Regex(@"^\s*fi\b")
let functionOpenRx = Regex(@"^[A-Za-z_][A-Za-z0-9_]*\(\) \{\s*$")
let functionCloseRx = Regex(@"^\}\s*$")
let laneNameRx = Regex(@"^[a-z][a-z0-9-]*$")

/// The partition check over the script TEXT. Returns every problem found, in
/// line order; an empty list is a clean partition.
let checkPartition (scriptText: string) (listed: string list) : string list =
    let problems = ResizeArray<string>()
    // Some lane for a lane block, None for a plain `if`.
    let stack = ResizeArray<string option>()
    let used = HashSet<string>()
    let mutable inFunction = false
    let laneOnStack () = stack |> Seq.exists Option.isSome
    let judgeInvocation (number: int) (line: string) (stripped: string) =
        if invocationRx.IsMatch line && not (laneOnStack ()) then
            problems.Add("line " + string number + ": gate invocation outside any lane block: " + stripped)
    let lines = scriptText.Split('\n')
    for i in 0 .. lines.Length - 1 do
        let number = i + 1
        let line = lines.[i].TrimEnd('\r')
        let stripped = line.Trim()
        if stripped.StartsWith("#") then ()
        elif functionOpenRx.IsMatch line then inFunction <- true
        elif inFunction then
            if functionCloseRx.IsMatch line then inFunction <- false
        else
            let m = laneOpenRx.Match line
            if m.Success then
                let lane = m.Groups.[1].Value
                if not (List.contains lane listed) then
                    problems.Add("line " + string number + ": block names unlisted lane '" + lane + "'")
                if laneOnStack () then
                    problems.Add("line " + string number + ": lane block nested inside another lane block")
                stack.Add(Some lane)
                used.Add lane |> ignore
            elif anyIfRx.IsMatch line then
                // The condition itself may be the invocation: `if ! ./scripts/x.sh; then`.
                // Judged against the enclosing block, before a plain block opens.
                judgeInvocation number line stripped
                if not (oneLineIfRx.IsMatch line) then stack.Add None
            elif elseRx.IsMatch line then
                // An else arm directly on a lane block runs in every lane EXCEPT the
                // named one — a block that is in no lane and in all lanes at once.
                if stack.Count > 0 && (stack.[stack.Count - 1]).IsSome then
                    problems.Add("line " + string number + ": lane block has an else/elif arm")
                judgeInvocation number line stripped
            elif fiRx.IsMatch line then
                if stack.Count = 0 then
                    problems.Add("line " + string number + ": 'fi' without an open 'if'")
                else
                    stack.RemoveAt(stack.Count - 1)
                // `fi; ./scripts/x.sh` — whatever follows belongs to the block outside.
                judgeInvocation number line stripped
            else
                judgeInvocation number line stripped
    if stack.Count > 0 then problems.Add "end of file: unclosed 'if' block(s)"
    for lane in listed do
        if not (used.Contains lane) then problems.Add("listed lane '" + lane + "' owns no block")
    List.ofSeq problems

let fail (msg: string) : 'a =
    eout ("FAIL: " + msg)
    exitWith 1

[<EntryPoint>]
let main argv =
    if argv.Length > 1 then fail "usage: audit-gate-lanes [path/to/build-and-test.sh]"
    let scriptPath = if argv.Length = 1 then argv.[0] else "scripts/build-and-test.sh"
    if not (File.Exists scriptPath) then
        fail ("gate script not found: " + scriptPath + " (run from the repository root)")
    let root = Directory.GetCurrentDirectory()
    // `runIn` resolves a relative program path that contains a separator against
    // `dir`; the default path does, so it is the script under audit, not a PATH
    // lookup. A rooted path is used as given.

    out "=== gate lanes: the script lists its lanes ==="
    let listing = runIn root [] scriptPath [ "--list-lanes" ]
    if listing.Rc <> 0 then fail ("--list-lanes exited " + string listing.Rc + ": " + javaTrim listing.Err)
    let listed = splitLines listing.Out |> List.filter (fun l -> not (blank l)) |> List.map javaTrim
    if listed.IsEmpty then fail "--list-lanes printed nothing"
    for lane in listed do
        if not (laneNameRx.IsMatch lane) then
            fail ("--list-lanes printed a name outside [a-z0-9-]: '" + lane + "'")
    if (listed |> List.distinct |> List.length) <> listed.Length then
        fail ("--list-lanes printed a duplicate lane: " + String.Join(" ", listed))
    out ("  lanes: " + String.Join(" ", listed))

    out "=== gate lanes: an unknown lane is refused before anything runs ==="
    let refusal = runIn root [ ("FOGELL_GATE_LANES", "no-such-lane") ] scriptPath []
    let refusalText = refusal.Out + refusal.Err
    if refusal.Rc <> 2 then
        fail ("unknown lane exited " + string refusal.Rc + ", expected 2: " + javaTrim refusalText)
    if not (refusalText.Contains "unknown gate lane 'no-such-lane'") then
        fail ("unknown-lane refusal did not name the lane: " + javaTrim refusalText)
    if refusalText.Contains "=== sdk ===" then fail "the gate started running before refusing the unknown lane"
    out "  refused (exit 2), nothing ran"

    out "=== gate lanes: the partition checker fails on planted defects ==="
    let scriptText = slurp scriptPath
    // Apply one textual edit to the real script and require the checker to REJECT
    // the result with a message naming the defect. EVERY occurrence is replaced:
    // the unused-lane arm must rename all of a lane's blocks.
    let mutant (name: string) (target: string) (replacement: string) (expected: string) =
        if not (scriptText.Contains target) then
            fail ("mutation target not found for '" + name + "': " + target)
        let mutated = scriptText.Replace(target, replacement)
        let problems = checkPartition mutated listed
        if problems.IsEmpty then fail ("partition checker accepted mutant '" + name + "'")
        if not (problems |> List.exists (fun p -> p.Contains expected)) then
            fail ("mutant '" + name + "' rejected for the wrong reason: " + String.Join(" | ", problems))
        out ("  killed: " + name)
    let anchor = "if lane_active lanes; then"
    // a proof invocation planted outside every lane block
    mutant "outside-lane" anchor ("./scripts/prove-project-tests.sh --planted\n" + anchor)
        "gate invocation outside any lane block"
    // a block filed under a lane the script does not list
    mutant "unlisted-lane" anchor "if lane_active lanez; then" "names unlisted lane 'lanez'"
    // a listed lane that no block uses: rename every audits block away
    mutant "unused-lane" "if lane_active audits; then" "if lane_active build; then" "listed lane 'audits' owns no block"
    // an extra `if` opened inside a lane and never closed, so the lane's own `fi`
    // closes the wrong block and the file ends with the lane still open
    mutant "unclosed-if" anchor (anchor + "\nif true; then") "unclosed 'if' block(s)"
    // the invocation IS the if-condition, outside every lane
    mutant "if-line-invocation" anchor
        ("if ! ./scripts/prove-project-tests.sh --planted; then exit 1; fi\n" + anchor)
        "gate invocation outside any lane block"
    // the invocation sits on the elif arm of a MULTI-LINE plain if, outside every
    // lane — multi-line so the `if` branch cannot catch it and only the elif
    // branch's own judgement can (a one-line form would prove nothing here)
    mutant "elif-line-invocation" anchor
        ("if false; then\n  :\nelif ! ./scripts/prove-project-tests.sh --planted; then\n  exit 1\nfi\n" + anchor)
        "gate invocation outside any lane block"
    // the invocation trails a `fi` on the same line, outside every lane
    mutant "fi-line-invocation" anchor
        ("if false; then :\nfi; ./scripts/prove-project-tests.sh --planted\n" + anchor)
        "gate invocation outside any lane block"
    // spellings that hide the script path from a start-of-line match
    mutant "prefixed-invocation" anchor
        ("planted=\"$(FOO=1 timeout 60 scripts/prove-project-tests.sh)\"\n" + anchor)
        "gate invocation outside any lane block"
    // an else arm on a lane block: runs in every lane but the named one
    mutant "else-arm" anchor (anchor + "\n  :\nelse\n  ./scripts/prove-project-tests.sh --planted")
        "lane block has an else/elif arm"

    out "=== gate lanes: the real script partitions cleanly ==="
    let problems = checkPartition scriptText listed
    if not problems.IsEmpty then
        for p in problems do eout ("  " + p)
        fail "build-and-test.sh has blocks outside the lane partition"
    out ("GATE-LANE AUDIT PASS: " + string listed.Length
         + " lanes; every invocation this audit recognises sits inside one lane block, no lane block has an else arm, every lane owns a block, unknown lanes are refused, 9 checker mutants killed")
    0
