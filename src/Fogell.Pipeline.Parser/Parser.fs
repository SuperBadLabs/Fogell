module Fogell.Pipeline.Parser.Parser

open FParsec
open Fogell.Ir
open Fogell.Admission
open Fogell.Pipeline.Parser.Lexeme

// ---------------------------------------------------------------------------
// Steps
// ---------------------------------------------------------------------------

/// A step call. Four shapes appear in real Jenkinsfiles:
///   command form            sh 'make'
///   paren form              sh(script: 'make', returnStdout: true)
///   named args, no parens   archiveArtifacts artifacts: '*.jar', fingerprint: true
///   block form              withEnv(['A=1']) { … }  /  script { … }
///
/// Argument *values* are captured as source text, not evaluated (ADR 0002).
let private stepParser, private stepRef = createParserForwardedToRef<Step, unit> ()

/// A named argument, carrying whether its value was SINGLE-quoted (literal) so a step
/// that renders text itself can honour Groovy's quoting. See Step.LiteralNamedArgs.
/// FG-138. A raw (unquoted) argument value: single- and double-quoted SPANS consumed
/// whole via `Lexeme.stringSpanRaw`, slashy spans consumed whole when position says
/// slashy (FG-141), everything else scanned to a stop character. Dollar-slashy
/// stays unparsed everywhere.
///
/// The stop set can therefore include `;` without truncating an expression that
/// carries one INSIDE a literal — `env.PART + '; echo b'`, `"printf 'x\"; …'"`,
/// and since FG-141 a slashy `/; …/` too. Five review rounds on FG-134 each found another string
/// form a hand-rolled character test had missed, and two of the intermediate
/// states produced SILENT no-ops. `Lexeme` knew MORE forms than the hand-rolled test
/// did, and asking it beat enumerating again.
let private rawArgValue (stops: char list) : P<string> =
    // A `/` IS DIVISION OR A SLASHY OPENER, DECIDED BY POSITION — see `slash`
    // below. The history that shaped the rule, kept because both failure
    // directions actually shipped:
    //
    // Treating EVERY `/` as a span opener was an APPROVAL BYPASS. `input
    // message: 10 / 2` — plain division — sent `/` into `stringSpanRaw`, which
    // found no closing delimiter, so the argument failed to parse; `steps` is
    // wrapped in `attempt`, that failure backtracked, the section became
    // EMPTY, and the build reported SUCCESS having never published a prompt.
    // UNPROVEN BY RECEIPT — the differential compares OUTPUT and WORKSPACE,
    // and neither shows whether a prompt was published at all. ASSERTED BY
    // `scripts/run-approval-lane.sh` scenario W, proven to fail: restoring
    // the every-`/`-opens assumption gives `FAIL: a gate with an expression
    // argument published NO prompt`. A human gate silently skipped is the
    // guarantee FG-046b exists to hold.
    //
    // Treating NO `/` as an opener — the interim FG-141 state — truncated a
    // slashy at any stop character inside it and refused valid pipelines.
    // The position test (one character of lookbehind) is what lets both
    // scenario W and the slashy gates Z2/Z3 hold at once.
    let plain: P<string> =
        many1Satisfy (fun c -> not (List.contains c stops) && c <> '\'' && c <> '"' && c <> '/')

    let quotedSpan: P<string> = attempt (lookAhead (anyOf "'\"") >>. stringSpanRaw)

    // FG-141. The context this scanner "does not have" is one character of
    // lookbehind: a `/` after something that can END an expression is division
    // and stays ordinary text — `input message: 10 / 2` is scenario W's
    // approval bypass and must never change reading. A `/` with NO left
    // operand can only open a slashy, whose span is consumed whole so a stop
    // character inside it (`/a}b/`, `/a;b/`) cannot truncate the argument.
    // The span must CLOSE ON ITS OWN LINE — a raw argument is line-bounded,
    // and letting a candidate span hunt across lines for a closer would
    // swallow later statements on a typo. Unterminated falls back to the
    // ordinary character, the exact pre-FG-141 reading.
    let slash: P<string> =
        fun stream ->
            if stream.Peek() <> '/' then
                Reply(Error, expected "'/'")
            else
                let here = stream.Index
                let mutable i = here - 1L
                let mutable lastSig = ' '
                let mutable scanning = true

                while scanning && i >= 0L do
                    stream.Seek i
                    let ch = stream.Peek()

                    if ch = ' ' || ch = '\t' then
                        i <- i - 1L
                    else
                        lastSig <- ch
                        scanning <- false

                stream.Seek here

                if Lexeme.endsExpression lastSig then
                    stream.Skip()
                    Reply("/") // division: the ordinary character
                else
                    let sb = System.Text.StringBuilder()
                    sb.Append(stream.Read(1)) |> ignore
                    let mutable closed = false
                    let mutable bad = false

                    while not closed && not bad && not stream.IsEndOfStream do
                        let d = stream.Peek()

                        if d = '\\' then
                            sb.Append(stream.Read(1)) |> ignore
                            if not stream.IsEndOfStream then sb.Append(stream.Read(1)) |> ignore
                        elif d = '/' then
                            sb.Append(stream.Read(1)) |> ignore
                            closed <- true
                        elif d = '\n' then
                            bad <- true
                        else
                            sb.Append(stream.Read(1)) |> ignore

                    if closed then
                        Reply(sb.ToString())
                    else
                        stream.Seek here
                        stream.Skip()
                        Reply("/")

    many1Strings (quotedSpan <|> plain <|> slash)

/// A string literal wins ONLY when it is the WHOLE value.
///
/// FG-142. `input message: 'Deploy ' + env.TARGET` matched `'Deploy '` as a
/// complete literal, left `+ env.TARGET` unconsumed, and the step block then
/// backtracked to EMPTY — the gate never published a prompt and the build
/// reported SUCCESS. An APPROVAL BYPASS, and pre-existing: merged main does the
/// same. A concatenated prompt message is an ordinary thing to write.
///
/// The lookahead is what makes the literal branch honest: it may only claim the
/// value if what follows is an argument terminator, otherwise the raw scanner
/// takes the whole expression — quoted spans included, which it now handles.
/// FG-141/142. A string literal wins ONLY when it is the WHOLE argument. When an
/// operator follows it, the literal is a PREFIX of a larger expression and must go
/// to `rawArgValue` instead — otherwise `input "Deploy " + env.TARGET` consumes only
/// `"Deploy "`, leaves `+ env.TARGET`, backtracks `steps` to EMPTY, and the build
/// reports SUCCESS having published NO approval prompt. Three separate routes to
/// that bypass were fixed one at a time before this was stated as a class.
///
/// The check is a NEGATIVE one on operators, not a positive one on terminators.
/// MEASURED, after two wrong fixes: `stringLiteralWithKindBoth` is `lexeme`-wrapped,
/// so it has ALREADY consumed trailing whitespace — including the newline — before
/// this guard runs. A terminator whitelist therefore saw the NEXT STEP'S IDENTIFIER
/// and rejected the literal, sending `sh 'printf "\033[31m"'` through the raw
/// scanner UNDECODED (`+ printf 033[31mred033[0m`) and undoing FG-122. Only non-last
/// steps broke, because `}` and `;` were in the whitelist and an identifier was not;
/// receipts `sh-octal-escape`, `sh-escape-edges` and the two `gstring-*` cases caught
/// it. Whitespace consumption inside the literal parser was the fact both earlier
/// fixes assumed away.
let private wholeValue (p: P<'a>) : P<'a> =
    // Chars that can CONTINUE an expression after a literal: arithmetic and string
    // concatenation, member access, indexing, comparison, regex and ternary. A step
    // name never starts with one, so a following step still terminates the argument.
    let continuation: P<unit> = skipAnyOf "+-*/%.?:=<>!&|^~["

    attempt (p .>>? notFollowedBy continuation)

/// FG-174. THE ONE DUPLICATE RULE, for every named-argument surface in this parser.
///
/// Groovy assembles named arguments into a MAP LITERAL and a duplicate key does not
/// survive it, so Jenkins rejects the pipeline at COMPILE time and runs NOTHING.
/// MEASURED on the pinned lab across four spellings — a step call parenthesised and in
/// command form, `when { equals … }`, and `when { environment … }` — each failing with
/// nothing in the log but `Started by user unknown or anonymous` and an EMPTY workspace.
/// Fogell took the FIRST matching key every time, and in the `when` cases ran the earlier
/// stage before quietly skipping the gated one. UNPROVEN by receipt: a compile-shaped
/// refusal emits nothing comparable (FG-129).
///
/// IT LIVES HERE, ABOVE EVERY CALLER, ON PURPOSE. The first version of this fix guarded
/// only step arguments, and the review found `when { equals actual: 1, actual: 2 }` the
/// very next round — the same defect on a surface I had not enumerated.
///
/// WHICH SURFACES THIS RULE REACHES, corrected after the sentence here claimed more than
/// the code delivers — the enumeration was right about the SHAPES and wrong about their
/// consequence, which is the overclaim class this project treats as a defect:
///   - STEP ARGUMENTS: guarded, and the refusal REACHES the caller. That path has no
///     fallback above it, so the pipeline is rejected — which is what Jenkins does.
///   - `when { equals … }` and `when { environment … }`: guarded here, but the refusal is
///     swallowed by `whenSectionOpaque` and the stage merely fails closed. FG-175.
///   - `tag`, `branch`, `changelog`, `triggeredBy`: these parse ONE named argument, and I
///     wrote that they therefore "cannot duplicate one". They can. `when { tag pattern:
///     'v1', pattern: 'v2' }` leaves the second pair UNCONSUMED, the condition fails, and
///     the same opaque fallback absorbs the section — identical outcome, reached by a
///     different route. Not guarded, and guarding them here would change nothing while
///     the fallback stands. FG-175 covers them, and each has a tripwire test.
/// A new list-shaped surface must call this.
let private firstDuplicateName (names: string list) : string option =
    names
    |> List.countBy id
    |> List.tryPick (fun (name, count) -> if count > 1 then Some name else None)

/// Refuses the list if any key repeats, naming the key.
let private rejectingDuplicates (keyOf: 'a -> string) (items: 'a list) : P<'a list> =
    match firstDuplicateName (items |> List.map keyOf) with
    | Some name ->
        fail
            $"duplicate named argument `{name}`: Groovy builds named arguments as a map literal, so Jenkins rejects the pipeline before running anything"
    | None -> preturn items

let private namedArgWithKind: P<string * string * string * bool> =
    attempt (
        identifier .>>? symbol ":" .>>. (
            (wholeValue stringLiteralWithKindBoth
             |>> fun (plain, escaped, interpolates) -> plain, escaped, not interpolates)
            <|> (rawArgValue [ ','; ')'; '\n'; '}'; ';' ] .>> ws
                 |>> fun s -> s.Trim(), "\u0001" + s.Trim(), false)))
    |>> fun (n, (v, escaped, isLiteral)) -> n, v, escaped, isLiteral

let private namedArg: P<string * string> =
    attempt (
        identifier .>>? symbol ":" .>>. (
            (stringLiteral |>> id)
            <|> (rawArgValue [ ','; ')'; '\n'; '}'; ';' ] .>> ws
                 |>> fun s -> s.Trim())))

/// A positional argument with its quote kind. `input 'Deploy ${TARGET}?'` is LITERAL on
/// Jenkins, and the positional form is as common as the named one — provenance was
/// tracked for named arguments only, so every positional message was interpolated.
/// FG-142, POSITIONAL. The terminator guard was applied to the NAMED branch only,
/// so `input "Deploy " + env.TARGET` still consumed just `"Deploy "`, left
/// `+ env.TARGET`, backtracked `steps` to EMPTY and shipped past the gate with no
/// prompt — a THIRD route to the same bypass, fixed one branch at a time because
/// I fixed the instance in front of me and described the class.
let private positionalArgWithKind: P<string * string * bool> =
    (wholeValue stringLiteralWithKindBoth
     |>> fun (plain, escaped, interpolates) -> plain, escaped, not interpolates)
    <|> (balancedRaw '[' ']' |>> fun v -> v, v, false)
    // A NESTED CALL is one value: `buildDiscarder(logRotator(numToKeepStr: '5'))`.
    // `rawArgValue` stops at `)`, so it consumed `logRotator(numToKeepStr: '5'` and
    // left a `)` that failed the `eof` in the reparse. Before FG-147 that failure was
    // downgraded and the options block still parsed; after it, the failure propagated,
    // `options` backtracked, and the TOP-LEVEL fallback swallowed the whole block —
    // so `timeout(time: 1, unit: 'SECONDS')` beside it was SILENTLY DROPPED and a
    // 5-second step ran to completion under a 1-second timeout. MEASURED against a
    // control (timeout alone aborts) and against merged `fe0b095` (aborts), so this
    // was a REGRESSION MY OWN FAIL-CLOSED INTRODUCED, invisible to receipt
    // `options-accept-and-ignore` because that case's options are ignored anyway.
    // MARKED AS AN EXPRESSION (`\u0001`), not a literal. Returning it unmarked kept it
    // out of `ExpressionArgs`, so rendering treated the SOURCE TEXT as the value: with
    // `input promptFactory()` the pending file read `prompt\tpromptFactory()` and a human
    // was asked to approve a function call as if it were the message. The sentinel path
    // already evaluates — `input 10 / 2` publishes `5` — so this branch was routing
    // around machinery that was working. MEASURED, approval-lane scenario Z5.
    //
    // This is the SEVENTH approval defect on this branch and the FOURTH caused by one of
    // my own fixes: FG-150 added this branch to stop `buildDiscarder(logRotator(...))`
    // dropping an options block, and in doing so opened a wrong-prompt path.
    <|> attempt (
        identifier .>>. balancedRaw '(' ')' .>> ws
        |>> fun (n, raw) -> n + raw, "\u0001" + n + raw, false)
    <|> (rawArgValue [ ','; ')'; '\n'; '{'; '}'; ';' ] .>> ws
         |>> fun s -> s.Trim(), "\u0001" + s.Trim(), false)

let private positionalArg: P<string> =
    stringLiteral
    <|> (balancedRaw '[' ']')
    <|> (rawArgValue [ ','; ')'; '\n'; '{'; '}'; ';' ] .>> ws
         |>> fun s -> s.Trim())

/// FG-134 / FG-138. A `;` TERMINATES a raw (unquoted) argument, outside literals.
///
/// This comment previously said the opposite, and was written when the terminator
/// was reverted — then left in place when FG-138 re-added it on this same branch.
/// Fourth time in this session I have reverted or re-landed a decision and left
/// the story about the other one; a stale rationale reads as intent and makes the
/// code look like the anomaly.
///
/// What holds now: literal SPANS are consumed whole by `Lexeme.stringSpanRaw`, so
/// `;` is a stop character for the raw text BETWEEN literals and never truncates
/// an expression carrying one inside quotes. Receipt
/// `steps-semicolon-after-raw-arg`, proven to discriminate by mutation.

let private argList
    : P<(string * string) list * string list * Set<string> * Set<int> * (string * string) list * Set<string> * string list> =
    let one =
        (namedArgWithKind |>> Choice1Of2) <|> (positionalArgWithKind |>> Choice2Of2)

    // FG-174. A DUPLICATE NAMED ARGUMENT IS REFUSED AT PARSE TIME, not at dispatch.
    //
    // Groovy builds a call's named arguments as a MAP LITERAL, and a duplicate key does
    // not survive it. MEASURED on the pinned lab in BOTH spellings — inside a `script`
    // block and at stage level — `sh(script: 'exit 7', returnStatus: true,
    // returnStatus: 'false')` makes Jenkins fail with nothing in the log but `Started by
    // user unknown or anonymous` and an EMPTY workspace: nothing ran at all. Fogell took
    // the first flag, suppressed the `exit 7`, ran the following step and reported
    // success. UNPROVEN by receipt — a compile-shaped refusal cannot be receipted, which
    // is FG-129 — so the probe and the parser tests carry it.
    //
    // WHY PARSE TIME, when refusing at dispatch would have been the smaller change:
    // Jenkins rejects the MODEL, so NO stage runs. A refusal at the step would let every
    // EARLIER stage run first — indistinguishable on this probe, where the duplicate sits
    // in the first step, and plainly wrong for a duplicate in a later stage. That
    // difference is between matching Jenkins and matching one example of it.
    sepBy one (symbol ",")
    // ONE implementation, not a second copy of the same rule. This inlined the check and
    // its error message, so the two could drift in wording or in keying — raised in
    // review on PR #53. Positionals cannot collide, so only the named ones are keyed,
    // and `rejectingDuplicates` is fed exactly those.
    >>= fun items ->
        items
        |> List.choose (function
            | Choice1Of2(n, _, _, _) -> Some n
            | _ -> None)
        |> rejectingDuplicates id
        |> fun guard ->
            guard
            >>. preturn (
            let namedWithKind = items |> List.choose (function Choice1Of2 x -> Some x | _ -> None)
            let named = namedWithKind |> List.map (fun (n, v, _, _) -> n, v)
            let literal = namedWithKind |> List.choose (fun (n, _, _, lit) -> if lit then Some n else None) |> Set.ofList
            // An UNQUOTED value is marked at capture; the marker never escapes this
            // function — it becomes membership of ExpressionArgs instead.
            let namedSource =
                namedWithKind |> List.map (fun (n, _, esc, _) -> n, esc.TrimStart '\u0001')

            let namedExpr =
                namedWithKind
                |> List.choose (fun (n, _, esc, _) -> if esc.StartsWith "\u0001" then Some n else None)
            let posWithKind = items |> List.choose (function Choice2Of2 x -> Some x | _ -> None)
            let pos = posWithKind |> List.map (fun (v, _, _) -> v)

            let literalPos =
                posWithKind
                |> List.mapi (fun i (_, _, lit) -> i, lit)
                |> List.choose (fun (i, lit) -> if lit then Some i else None)
                |> Set.ofList

            // The `#0`, `#1`… entries the Step doc describes. They were documented and
            // never produced, so a positional prompt fell back to the plain value and
            // expanded an escaped dollar.
            let posSource = posWithKind |> List.mapi (fun i (_, esc, _) -> $"#{i}", esc.TrimStart '\u0001')

            let posExpr =
                posWithKind
                |> List.mapi (fun i (_, esc, _) -> i, esc)
                |> List.choose (fun (i, esc) -> if esc.StartsWith "\u0001" then Some $"#{i}" else None)

            // keys in SOURCE order: named names and `#i` positionals as written
            let order =
                items
                |> List.fold
                    (fun (acc, pi) it ->
                        match it with
                        | Choice1Of2(n, _, _, _) -> n :: acc, pi
                        | Choice2Of2 _ -> $"#{pi}" :: acc, pi + 1)
                    ([], 0)
                |> fst
                |> List.rev

            named, pos, literal, literalPos, namedSource @ posSource, Set.ofList (namedExpr @ posExpr), order)

let private stepBlock: P<Step list> =
    // SEMICOLONS separate statements in Groovy, and a step block may use them:
    // `steps { sh 'a'; sh 'b' }` is ordinary Declarative that Jenkins runs.
    //
    // Without consuming them the failure was SILENT and total. `many` stopped at
    // the `;`, `between` then demanded `}` and found `;`, so the whole
    // `stepBlock` failed — and because the `steps` section is wrapped in
    // `attempt`, that failure backtracked and the section was simply never
    // picked. `Steps` defaulted to [], giving a stage with NO steps, no
    // diagnostic, and a SUCCESSFUL build. Neither step ran and nothing said so.
    // FG-134.
    //
    // Leading and trailing separators are allowed too — `{ ; sh 'a'; }` is legal
    // Groovy and refusing it would trade one wrong answer for another.
    let separators = skipMany (symbol ";")

    between (symbol "{") (symbol "}") (ws >>. separators >>. many (attempt (stepParser .>> separators)))

/// Whitespace WITHIN a line. Load-bearing: see the step parser below.
/// FG-145. A parenthesised argument body that FAILS to parse must not be downgraded
/// to a single positional argument when it plainly carries NAMED arguments.
///
/// The same silent-fallback shape as FG-143, one level down and NOT a bypass, which is
/// why it outlived that fix: a prompt IS published and the gate DOES wait. It publishes
/// the WRONG ONE. MEASURED, approval-lane scenario Z2:
/// `input(message: /Deploy; / + env.TARGET, ok: "Ship it")` failed to reparse, became one
/// positional argument, and the human was asked to approve the literal text
/// `message: /Deploy; / + env.TARGET, ok: "Ship it"` — not Jenkins' message value — while
/// `ok` was dropped entirely. MEASURED, approval-lane scenario Z2. An approval gate
/// that shows the operator different words
/// than the pipeline author wrote is not a working gate, even though it stops the build.
///
/// The downgrade stays for bodies with NO named-argument syntax, where treating the body
/// as one positional value is what the step means (`withEnv([...])`, `timeout(5)`).
/// FG-147. A parenthesised argument body that FAILS to parse is REFUSED. There is no
/// classification step, because every attempt to write one has been wrong.
///
/// The first guard tested a LEADING `ident:` while claiming to detect named syntax
/// anywhere. The second scanned for top-level `ident:` anywhere, skipping quoted spans
/// and brackets — and did not skip SLASHY spans, so `input(/Deploy { / + env.TARGET,
/// ok: "Ship it")` opened a brace that never closed, hid the top-level `ok:` behind a
/// non-zero depth, took the downgrade, and published the raw body as the prompt. The
/// scanner has to be right about the exact construct that made the body unparseable in
/// the first place, which is the same enumeration trap as the terminator whitelist that
/// broke FG-122: a list of what to skip is only as complete as my list of what exists.
///
/// The body already failed to parse. That is the whole fact: we do not understand it,
/// so we must not invent a value from it. Refusing needs no scanner and cannot be
/// incomplete. MEASURED, approval-lane scenarios Z2 and Z3.
///
/// Bodies that parse are unaffected — `withEnv([...])`, `timeout(5)`, `sh(script: 'x')`
/// never reach this branch. If a real Jenkinsfile ever needs an opaque paren body that
/// Fogell cannot parse, it must be a REFUSAL that gets a ticket, not a silent guess at
/// what the author meant.

let private parenArgs (raw: string) =
    let body = if raw.Length >= 2 then raw.Substring(1, raw.Length - 2) else ""

    match runParserOnString (ws >>. argList .>> eof) () "args" body with
    | ParserResult.Success(v, _, _) -> preturn v
    | ParserResult.Failure _ ->
        fail $"a parenthesised argument body that does not parse is refused, never downgraded to one positional value: ({body})"

let private hspaces: P<unit> = skipMany (anyOf " \t")

stepRef.Value <-
    ws
    >>. pipe3 position identifier
            // A step is EITHER `name(args)` OR `name args` — never both, and the
            // inline form must start ON THE SAME LINE.
            //
            // The previous version tried parenthesised args and then, independently,
            // inline args starting with `ws`. For `deleteDir()` that skipped the
            // NEWLINE and consumed the whole NEXT STEP as a positional argument —
            // which was then thrown away, because when parens are present the inline
            // args are ignored. So `deleteDir()` silently ATE the step after it and
            // the build reported success having skipped the work. Found by a
            // stash/unstash differential receipt: `before.txt` was missing from the
            // workspace, nothing else. Any zero-argument call form was affected —
            // `deleteDir()`, `cleanWs()`, and so on.
            (choice
                [ attempt (balancedRaw '(' ')') >>= parenArgs
                  attempt (hspaces >>. argList)
                  preturn ([], [], Set.empty, Set.empty, [], Set.empty, []) ])
            (fun pos name args -> pos, name, args)
    >>= (fun (pos, name, args) ->
        // FG-160. `script { … }` HOLDS SCRIPTED GROOVY, so its body is captured
        // VERBATIM instead of being parsed as Declarative steps. Parsing it as steps
        // read `if (cond)` as a step named `if` with a block, and `def x = 1` as a step
        // named `def` — structurally accepted, semantically nonsense, and the reason the
        // walker could never run one. `Fogell.Groovy.Parser` is what understands this
        // text; the walker hands it over at execution.
        if name = "script" then
            (attempt (balancedBody '{' '}') |>> fun raw -> pos, name, args, [], Some raw)
            <|> preturn (pos, name, args, [], None)
        else
            opt (attempt stepBlock) |>> fun b -> pos, name, args, defaultArg b [], None)
    |>> fun (pos, name, args, block, scriptBody) ->
            // The DOWNGRADE THAT USED TO LIVE HERE IS GONE, not merely guarded. It
            // re-parsed the paren body and, on failure, invented a single positional
            // argument from the raw text — which is how an approval prompt came to
            // show the operator `message: /Deploy; / + env.TARGET, ok: "Ship it"`.
            // `parenArgs` now refuses instead, so there is no second path to reach.
            let named, positional, literalNamed, literalPositional, interpolationSource, expressionArgs, argOrder =
                args

            { Name = name
              Positional = positional
              Named = named
              LiteralNamedArgs = literalNamed
              LiteralPositionalArgs = literalPositional
              InterpolationSource = interpolationSource
              ExpressionArgs = expressionArgs
              ArgumentOrder = argOrder
              Block = block
              // Only ever populated on the OPAQUE paren path, which no longer exists.
              // No code reads this field — `rg RawArgs` finds the record definition,
              // this assignment and one test literal — so emptying it drops nothing.
              RawArgs = ""
              ScriptBody = scriptBody
              Position = pos }

// ---------------------------------------------------------------------------
// Sections
// ---------------------------------------------------------------------------

/// `NAME = value` lines inside environment { } / tools { }, carrying whether the
/// value interpolates. An unquoted value is a Groovy expression, so it does.
let private keyValueBodyWithKind: P<(string * string * bool) list> =
    many (
        attempt (
            ws
            >>. identifier
            .>> symbol "="
            .>>. (stringLiteralWithKind
                  <|> (many1Satisfy (fun c -> c <> '\n') |>> fun s -> s.Trim(), true))
            .>> ws
            |>> fun (n, (v, interpolates)) -> n, v, interpolates))

let private keyValueBody: P<(string * string) list> =
    keyValueBodyWithKind |>> List.map (fun (n, v, _) -> n, v)

let private environmentSection: P<(string * string * bool) list> =
    keyword "environment" >>. between (symbol "{") (symbol "}") keyValueBodyWithKind

/// Declarative `tools` entries use command form, not environment assignment form:
///
///     tools { maven 'm3'; jdk 'j8' }
///
/// FG-014. The original parser reused `keyValueBody`, so at pipeline scope it accepted only
/// `maven = 'm3'` and rejected the valid Jenkins spelling in six pinned corpus
/// files; stage scope kept that old parser after the first slice. Keep the old
/// assignment spelling, including its newline-terminated unquoted-value fallback,
/// as an explicit all-assignment compatibility lane — this slice must not silently
/// narrow the old parser surface. The strict command/mixed lane is shared at both
/// scopes and cannot weaken that legacy lane's adjacency behavior.
///
/// DIRECTLY PROBED on Jenkins 2.568.1 after a confounded compact-pipeline probe was
/// discarded: semicolon and newline forms each reach Declarative validation as
/// TWO entries at pipeline and stage scope. Space-only `maven 'm3' jdk 'j8'`
/// reaches validation as only the SECOND entry — Groovy associates it differently;
/// it is not two adjacent tool declarations. Fogell refuses that ambiguous shape
/// rather than inventing two tools Jenkins did not model. A command kind and value
/// split across a newline is refused by Jenkins at both scopes (`Expected to find
/// someTool someVersion`; `No tools specified`); only horizontal space or a tab may
/// join the two tokens here.
let private toolCommandGap: P<unit> = skipMany1 (anyOf " \t")

let private toolEntry: P<string * string> =
    attempt (identifierBare .>> toolCommandGap .>>. stringLiteralBare)
    <|> attempt ((
            identifier
            .>> symbol "="
            .>>. (stringLiteralBare <|> (many1Satisfy (fun c -> c <> '\n') |>> fun s -> s.Trim()))))

/// Trivia after one entry is a boundary only when it contains a newline or is
/// followed by a semicolon. `ws` normally erases that distinction; capture its
/// exact skipped source before deciding. Leading/trailing separators remain legal,
/// matching the existing Groovy-body parsers.
let private toolSeparator: P<unit> =
    withSkippedString (fun skipped () -> skipped) ws
    >>= fun skipped ->
        if skipped.Contains '\n' || skipped.Contains '\r' then
            skipMany (skipChar ';' >>. ws)
        else
            skipMany1 (skipChar ';' >>. ws)

let private toolEntryEnd: P<unit> =
    attempt toolSeparator
    <|> (ws >>. lookAhead (skipChar '}'))

let private strictToolsBody: P<(string * string) list> =
    ws
    >>. skipMany (skipChar ';' >>. ws)
    >>. many (attempt (toolEntry .>> toolEntryEnd))

let private toolsSection: P<(string * string) list> =
    keyword "tools"
    >>. (attempt (between (symbol "{") (symbol "}") keyValueBody)
         <|> between (symbol "{") (symbol "}") strictToolsBody)

// The legacy arm is intentionally WHOLE-BODY: if any command-form entry appears,
// its closing brace cannot match and `attempt` returns to the strict mixed lane.
// That preserves adjacent quoted assignments exactly, while a mixed assignment +
// command still needs the newline/semicolon boundary enforced by [toolEntryEnd].

let private agentSpec: P<AgentSpec> =
    let inner =
        choice
            [ attempt (keyword "any" >>% AgentAny)
              attempt (keyword "none" >>% AgentNone)
              attempt (keyword "label" >>. stringLiteral |>> AgentLabel)
              attempt (
                  keyword "docker"
                  >>. (attempt (between (symbol "{") (symbol "}") (
                          ws >>. many (attempt (identifier .>>. (stringLiteral <|> (many1Satisfy (fun c -> c <> '\n') |>> fun s -> s.Trim())) .>> ws)))
                       |>> fun kvs ->
                               let img = kvs |> List.tryPick (fun (k, v) -> if k = "image" then Some v else None)
                               AgentDocker(defaultArg img "", None))
                       <|> (stringLiteral |>> fun img -> AgentDocker(img, None))))
              attempt (keyword "dockerfile" >>. opt (balancedBody '{' '}') |>> fun _ -> AgentDockerfile None)
              (identifier .>> opt (attempt (balancedRaw '{' '}')) |>> AgentUnmodelled) ]

    keyword "agent"
    >>. (attempt (between (symbol "{") (symbol "}") (ws >>. inner))
         <|> inner)

let private postConditionName: P<PostCondition> =
    choice
        [ attempt (keyword "always" >>% Always)
          attempt (keyword "success" >>% Success)
          attempt (keyword "failure" >>% Failure)
          attempt (keyword "unstable" >>% Unstable)
          attempt (keyword "aborted" >>% Aborted)
          attempt (keyword "changed" >>% Changed)
          attempt (keyword "cleanup" >>% Cleanup)
          attempt (keyword "fixed" >>% Fixed)
          attempt (keyword "regression" >>% Regression)
          attempt (keyword "notBuilt" >>% NotBuilt) ]

let private postSection: P<(PostCondition * Step list) list> =
    keyword "post"
    >>. between (symbol "{") (symbol "}") (
        ws >>. many (attempt (postConditionName .>>. stepBlock)))

/// `when { environment name: 'FOO', value: 'bar' }`.
///
/// MEASURED shape. The first version expected `environment FOO = 'bar'`, which
/// Jenkins does not accept — so every real `environment` condition fell through
/// to the unmodelled branch, and from there (see below) out of the `when`
/// section entirely. Named arguments, in either order.
/// Receipt: `when-conditions`.
let private whenEnvironmentCondition: P<WhenCondition> =
    keyword "environment"
    >>. (sepBy1 (identifier .>> symbol ":" .>>. stringLiteral) (symbol ",") >>= rejectingDuplicates fst)
    |>> fun pairs ->
            let get k = pairs |> List.tryPick (fun (n, v) -> if n = k then Some v else None)

            match get "name", get "value" with
            | Some n, Some v -> WhenEnvironment(n, v)
            | _ ->
                // Recognised the keyword but not its arguments: unmodelled, so
                // evaluation fails closed rather than assuming a direction.
                WhenUnmodelled("environment", pairs |> List.map (fun (n, v) -> $"{n}: {v}") |> String.concat ", ")

/// `when { tag 'v*' }` — also accepts the named form `tag pattern: 'v*'`.
/// REVIEW FIX (Copilot, PR #13): the named form accepted ANY key, so
/// `tag comparator: 'REGEXP'` was read as pattern = "REGEXP" — a silently wrong
/// gate. Only `pattern:` is accepted; anything else is unmodelled and fails closed.
let private whenTagCondition: P<WhenCondition> =
    keyword "tag"
    >>. ((attempt (identifier .>> symbol ":" .>>. stringLiteral)
          |>> fun (k, v) -> if k = "pattern" then WhenTag v else WhenUnmodelled("tag", $"{k}: {v}"))
         <|> (stringLiteral |>> WhenTag))
    .>> ws

/// `when { equals expected: 2, actual: 2 }` — a pure comparison, so it is worth
/// modelling rather than failing closed on.
let private whenEqualsCondition: P<WhenCondition> =
    keyword "equals"
    // Operands keep their SOURCE form, quotes included, so a quoted "2" and a bare
    // 2 are distinguishable — Jenkins compares objects, and String != Integer.
    >>. sepBy1
            (identifier .>> symbol ":"
             .>>. (attempt (stringLiteral |>> fun v -> $"'{v}'")
                   // `_` belongs here: the last unmodelled `when` in the corpus was
                   // `equals expected: 'False', actual: _deploy_to_nexus`, and omitting
                   // underscore from an IDENTIFIER charset made that whole condition
                   // unmodelled — so the stage failed closed over one character.
                   <|> (many1Satisfy (fun c -> isDigit c || c = '-' || c = '.' || c = '_' || isLetter c) .>> ws)))
            (symbol ",")
    >>= rejectingDuplicates fst
    .>> ws
    |>> fun pairs ->
            let get k = pairs |> List.tryPick (fun (n, v) -> if n = k then Some v else None)

            match get "expected", get "actual" with
            | Some e, Some a -> WhenEquals(e, a)
            | _ -> WhenUnmodelled("equals", pairs |> List.map (fun (n, v) -> $"{n}: {v}") |> String.concat ", ")

/// A `;` between conditions. `anyOf { branch 'a'; branch 'b' }` is idiomatic and
/// appeared in 6 corpus files, where `many whenCondition` stopped at the semicolon, the
/// closing brace failed to match, and the WHOLE anyOf degraded to unmodelled — so those
/// stages failed closed.
let private whenSeparators: P<unit> = skipMany (skipChar ';' >>. ws)

/// `()` and nothing else. Accepting arbitrary contents and throwing them away is how a
/// form Jenkins REJECTS gets silently executed.
let private emptyParens: P<unit> = symbol "(" >>. symbol ")"

/// A condition taking one value, written bare (`changeset '**/*.java'`) or named with
/// its ONE legal key.
///
/// MEASURED, after inventing the key names once already: Jenkins ACCEPTS
/// `changeset pattern:` and `changelog pattern:` and REJECTS `changeset glob:` with a
/// compilation error — so the invented `glob`/`regexp` keys made Fogell accept what
/// Jenkins refuses AND fail closed on the form real Jenkinsfiles use. `triggeredBy cause:`
/// was measured correct. Guessing a data-bound parameter name is not a small thing: it
/// inverts the gate in both directions at once. A different key is returned as raw text so the caller can record
/// it unmodelled rather than mistake it for the value.
/// Receipt: `when-scm-pattern-keys`.
let private namedOrBare (key: string) : P<Result<string, string>> =
    // `Ok`/`Error` unqualified resolve to FParsec's ReplyStatus here, not Result — the
    // same shadowing that bit this file once before.
    (attempt (identifier .>> symbol ":" .>>. stringLiteral)
     |>> fun (k, v) -> if k = key then Result.Ok v else Result.Error $"{k}: {v}")
    <|> (stringLiteral |>> Result.Ok)

let rec private whenCondition: P<WhenCondition> =
    parse {
        let! _ = ws
        return! choice
                    // Same key validation as `tag`: a named argument that is not
                    // `pattern:` must not be mistaken for the branch pattern.
                    [ // FG-048b. Context-dependent conditions.
                      //
                      // REVIEW FIXES (both reviewers, PR #16): these accepted ANY balanced
                      // parentheses and DISCARDED the contents, so `buildingTag('x')` and
                      // `changeRequest(target: 'main')` were silently modelled as their
                      // argument-free forms — Jenkins rejects the first and applies a
                      // filter for the second. Only genuinely EMPTY parens are accepted;
                      // anything inside falls through to unmodelled and fails closed.
                      // Named forms validate their key, the same rule already applied to
                      // `tag` and `branch`, so a wrong key cannot become the pattern.
                      attempt (keyword "buildingTag" >>. opt (attempt emptyParens) .>> ws >>% WhenBuildingTag)
                      attempt (keyword "changeRequest" >>. opt (attempt emptyParens) .>> ws >>% WhenChangeRequest)
                      attempt (keyword "isRestartedRun" >>. opt (attempt emptyParens) .>> ws >>% WhenIsRestartedRun)
                      attempt (keyword "changeset" >>. namedOrBare "pattern" .>> ws |>> function Result.Ok v -> WhenChangeset v | Result.Error raw -> WhenUnmodelled("changeset", raw))
                      attempt (keyword "changelog" >>. namedOrBare "pattern" .>> ws |>> function Result.Ok v -> WhenChangelog v | Result.Error raw -> WhenUnmodelled("changelog", raw))
                      attempt (keyword "triggeredBy" >>. namedOrBare "cause" .>> ws |>> function Result.Ok v -> WhenTriggeredBy v | Result.Error raw -> WhenUnmodelled("triggeredBy", raw))
                      attempt (
                          keyword "branch"
                          >>. ((attempt (identifier .>> symbol ":" .>>. stringLiteral)
                                |>> fun (k, v) -> if k = "pattern" then WhenBranch v else WhenUnmodelled("branch", $"{k}: {v}"))
                               <|> (stringLiteral |>> WhenBranch))
                          .>> ws)
                      attempt whenTagCondition
                      attempt whenEqualsCondition
                      attempt whenEnvironmentCondition
                      attempt (keyword "expression" >>. balancedBody '{' '}' .>> ws |>> WhenExpression)
                      attempt (keyword "allOf" >>. between (symbol "{") (symbol "}") (whenSeparators >>. many (attempt (whenCondition .>> whenSeparators))) .>> ws |>> WhenAllOf)
                      attempt (keyword "anyOf" >>. between (symbol "{") (symbol "}") (whenSeparators >>. many (attempt (whenCondition .>> whenSeparators))) .>> ws |>> WhenAnyOf)
                      attempt (keyword "not" >>. between (symbol "{") (symbol "}") whenCondition .>> ws |>> WhenNot)
                      // Unrecognised condition. `.>> ws` is load-bearing: without
                      // it this branch ends mid-line, the enclosing `}` fails to
                      // match, and the ENTIRE `when` section falls through to the
                      // stage's generic section fallback — which means a stage
                      // with an unparseable `when` runs unconditionally. Silently
                      // running a stage Jenkins skips is the worst failure mode
                      // available here, and it was live until a receipt exposed it.
                      ((identifier
                        .>>. (attempt (balancedRaw '{' '}')
                              <|> attempt (balancedRaw '(' ')')
                              // NOT `restOfLine`: on a single-line `when { unknown x: 'y' }`
                              // that swallowed the closing braces and destroyed the
                              // whole pipeline parse. Stop at the enclosing brace or
                              // the newline, whichever comes first.
                              <|> (manySatisfy (fun c -> c <> '\n' && c <> '}') |>> fun s -> s.Trim())))
                       .>> ws
                       |>> WhenUnmodelled) ]
    }

/// A `when { }` gate. Declarative allows SEVERAL direct conditions and combines
/// them with implicit all-of semantics.
///
/// REVIEW FIX (Codex, PR #13 round 3): only ONE condition was parsed, so
/// `when { environment name: 'A', value: '1'\n environment name: 'B', value: '2' }`
/// failed at the second, fell to the opaque backstop, and FAILED THE BUILD — where
/// Jenkins simply requires both.
/// `beforeAgent true` and friends are DIRECTIVES, legal only DIRECTLY under `when`.
///
/// MEASURED: Jenkins rejects one nested inside allOf/anyOf/not —
///   Unknown conditional beforeAgent. Valid conditionals are: allOf, anyOf, branch,
///   buildingTag, changeRequest, changelog, changeset, environment, equals, expression,
///   isRestartedRun, not, tag, triggeredBy
/// Parsing them as ordinary recursive conditions made `anyOf { beforeAgent true; branch
/// 'never' }` unconditionally TRUE here, running a stage on a pipeline Jenkins refuses to
/// COMPILE. That enumeration is also the authority for what a `when` may contain, and all
/// fourteen of those conditionals are modelled.
/// UNPROVEN BY RECEIPT: measured by probe, but the case was DROPPED from the suite —
/// both engines fail and only the narration differs (Jenkins prints a Groovy compile
/// report, Fogell a stage-gate error), so forcing it to PROVEN would have meant
/// pattern-matching a compiler's error layout. Held by a parser test instead.
let private whenDirective: P<unit> =
    (keyword "beforeAgent" <|> keyword "beforeInput" <|> keyword "beforeOptions")
    >>. ((stringReturn "true" ()) <|> (stringReturn "false" ()))
    .>> ws

/// One item directly under `when`: a directive (contributing nothing) or a condition.
let private whenItem: P<WhenCondition option> =
    (attempt (whenDirective >>% None)) <|> (whenCondition |>> Some)

let private whenSection: P<WhenCondition> =
    keyword "when"
    >>. between (symbol "{") (symbol "}") (ws >>. many1 (attempt whenItem))
    |>> fun items ->
            match items |> List.choose id with
            | [ single ] -> single
            // MEASURED: Jenkins REJECTS a `when` holding only directives —
            //   WorkflowScript: 5: Empty when closure, remove the property or add some
            //   content.
            // Treating it as "nothing to gate on, so run" executed a stage on a pipeline
            // Jenkins refuses to compile.
            // UNPROVEN BY RECEIPT: measured by probe; no case in the suite, for the same reason
            // as the nested-directive claim above — a rejection makes both engines fail and only
            // the narration differ. Held by a parser test.
            | [] -> WhenUnmodelled("when", "empty when closure")
            | multiple -> WhenAllOf multiple

/// Backstop. If the structured parse above fails for ANY reason, the `when`
/// must still be recorded — as unmodelled, so evaluation fails closed. It must
/// never be allowed to disappear and leave the stage unconditional.
let private whenSectionOpaque: P<WhenCondition> =
    keyword "when" >>. balancedRaw '{' '}' |>> fun raw -> WhenUnmodelled("when", raw)

// ---------------------------------------------------------------------------
// Stages
// ---------------------------------------------------------------------------

/// FG-153. Sections whose parse result FEEDS THE MODEL. If one of these does not
/// parse it must be REFUSED — never consumed by a fallback and dropped.
///
/// ONE SET, USED BY BOTH FALLBACKS, because two lists is the actual defect. The stage
/// fallback learned to refuse `steps` (FG-143); the top-level fallback later learned
/// `options`/`stages` (FG-150); `options` was never carried back to the stage one
/// (FG-152); and then `environment` was missing from BOTH — a malformed
/// `environment { FOO = "ok"; bogus(/x) y/) }` completed successfully with `FOO`
/// UNSET, so the shell ran without a variable the pipeline declares. Four rounds of
/// the same fix landing on one of two siblings. A shared set cannot drift.
///
/// `when` is absent DELIBERATELY: it has an opaque variant of its own, so consuming it
/// opaquely is a documented degradation rather than a silent loss. Names with no
/// dedicated parser (`libraries`, `matrix`, `axes`) fall through to opaque as before.
let private actedOnSections =
    set
        [ "agent"
          "environment"
          "tools"
          "options"
          "parameters"
          "triggers"
          "stages"
          "parallel"
          "post"
          "steps"
          // `input` IS A HUMAN GATE. It was mapped to an opaque section in the original
          // parser and then IGNORED by stage construction, so the DIRECTIVE form
          // `stage("Gate") { input { message "Deploy?" } steps { … } }` ran its steps
          // with NO PROMPT PUBLISHED and the build reported success. MEASURED,
          // proof case stage-input-directive:
          // prompts=0, `shipped.txt` present. Pre-existing since the first parser
          // commit, and invisible to the approval lane because every lane fixture uses
          // the STEP form `steps { input … }` — a whole syntax the guards never touched.
          //
          // Fogell does not implement the directive form. Until it does, this REFUSES:
          // an unsupported human gate must stop the build, never wave it through. That
          // is the sixth approval bypass on this branch and the only one that was not
          // mine. FG-155.
          "input" ]

let private stageParser, private stageRef = createParserForwardedToRef<Stage, unit> ()

let private stagesBody: P<Stage list> =
    between (symbol "{") (symbol "}") (ws >>. many (attempt stageParser))

/// `failFast true` — a STAGE-level directive, sibling of `parallel`.
///
/// MEASURED, not assumed. The first version parsed it INSIDE the `parallel { }`
/// block; real Jenkins 2.568.1 rejects that outright:
///
///   WorkflowScript: 9: Expected a stage @ line 9, column 17.
///   failFast true
///
/// A differential receipt caught it. Accepting a form the reference engine
/// refuses is not leniency — it means a pipeline that runs here fails there,
/// which is the exact opposite of the lift-and-shift promise.
/// UNPROVEN BY RECEIPT: measured on PR #12 from Jenkins' own rejection message; no case
/// in the suite, same rejection-narration reason. Held by a parser test.
let private failFastDirective: P<bool> =
    keyword "failFast" >>. ws >>. ((stringReturn "true" true) <|> (stringReturn "false" false))
    .>> ws

/// One `stage('Name') { … }`. Sections may appear in any order, so this
/// accumulates whatever it finds rather than demanding a fixed sequence — which
/// is also what makes an unknown section a *named* rejection instead of a
/// generic syntax error.
type private StageSection =
    | SecAgent of AgentSpec
    | SecEnv of (string * string * bool) list
    | SecSteps of Step list
    | SecWhen of WhenCondition
    | SecPost of (PostCondition * Step list) list
    | SecNested of Stage list * bool
    | SecFailFast of bool
    | SecOptions of Step list
    | SecOther of string

stageRef.Value <-
    ws
    >>. keyword "stage"
    >>. pipe2 position
            (attempt (between (symbol "(") (symbol ")") stringLiteral) <|> stringLiteral)
            (fun p n -> p, n)
    .>>. between (symbol "{") (symbol "}") (
        ws
        >>. many (
            attempt (
                choice
                    [ attempt (agentSpec |>> SecAgent)
                      attempt (environmentSection |>> SecEnv)
                      attempt (keyword "steps" >>. stepBlock |>> SecSteps)
                      attempt (whenSection |>> SecWhen)
                      attempt (whenSectionOpaque |>> SecWhen)
                      attempt (postSection |>> SecPost)
                      attempt (keyword "stages" >>. stagesBody |>> fun ss -> SecNested(ss, false))
                      attempt (keyword "parallel" >>. stagesBody |>> fun ss -> SecNested(ss, true))
                      attempt (failFastDirective |>> SecFailFast)
                      attempt (keyword "options" >>. stepBlock |>> SecOptions)
                      attempt (toolsSection |>> fun _ -> SecOther "tools")
                      attempt (keyword "matrix" >>. balancedRaw '{' '}' |>> fun _ -> SecOther "matrix")
                      attempt (keyword "axes" >>. balancedRaw '{' '}' |>> fun _ -> SecOther "axes")
                      // THE OPAQUE FALLBACK MUST NOT CLAIM `steps`. This is the ROOT of
                      // every approval bypass on this branch, not a fourth instance of
                      // them. When `stepBlock` failed for ANY reason the `attempt` above
                      // backtracked, this catch-all consumed `steps { … }` as an opaque
                      // section, and the stage ran with NO STEPS AT ALL while the build
                      // reported SUCCESS. MEASURED, approval-lane scenario Z, with `input message: /Deploy; / +
                      // env.TARGET`: prompts=0, the gate stage's workspace EMPTY, and the
                      // following stage's `after.txt` present — a human approval skipped
                      // and its guarded work dropped, silently.
                      //
                      // `when` has a DELIBERATE opaque variant above; `steps` has none,
                      // which is what marks swallowing it as unintended rather than a
                      // documented degradation. Refusing it here converts every
                      // unparseable step form — slashy, dollar-slashy, and whatever the
                      // grammar does not yet cover — from a SILENT BYPASS into a parse
                      // failure the admission layer reports. FG-134's hazard, at its source.
                      (identifier
                       >>= (fun n ->
                           // EVERY SECTION FOGELL ACTS ON, not just `steps`. This refused
                           // `steps` alone, and the TOP-LEVEL fallback was later taught to
                           // refuse `options`/`stages` — and the two were never reconciled,
                           // so a stage's `options { timeout(...); bogus(/x) y/) }` was
                           // still swallowed whole and its TIMEOUT DROPPED: measured
                           // `completed: success` against a control that aborts. That is
                           // the same fix landing on one of two sibling functions, which is
                           // the pattern this branch has repeated more than any other.
                           //
                           // `when` is deliberately absent: it has an opaque variant above,
                           // so consuming it opaquely is a documented degradation rather
                           // than a silent loss.
                           if actedOnSections.Contains n then
                               fail
                                   $"a `{n}` section that does not parse is refused, never consumed opaquely"
                           else
                               (attempt (balancedRaw '{' '}') <|> attempt (balancedRaw '(' ')'))
                               |>> fun _ -> SecOther n)) ])))
    |>> fun ((pos, name), sections) ->
            let pick f = sections |> List.tryPick f
            { Name = name
              Agent = pick (function SecAgent a -> Some a | _ -> None)
              Environment =
                defaultArg (pick (function SecEnv e -> Some(e |> List.map (fun (n, v, _) -> n, v)) | _ -> None)) []
              EnvironmentLiteralNames =
                defaultArg
                    (pick (function
                        | SecEnv e -> Some(e |> List.choose (fun (n, _, i) -> if i then None else Some n) |> Set.ofList)
                        | _ -> None))
                    Set.empty
              Steps = defaultArg (pick (function SecSteps s -> Some s | _ -> None)) []
            // EVERY `options` SECTION, not the first. `pick` is `tryPick`, so a
            // second `options { }` block was invisible: `options { buildDiscarder(...) }`
            // followed by `options { retry(2) }` parsed as the first alone, and the
            // FG-053 refusal never saw the directive it exists to catch — Fogell ran
            // a build Jenkins rejects. This is the same first-vs-all mistake this
            // file already fixed one level down, where `tryFind` over the ENTRIES of
            // one block accepted `timestamps(); timestamps(false)`; the section level
            // kept it. Collecting them concatenated also preserves the duplicate for
            // the arg validators, which is what makes the repeat detectable at all.
              Options = sections |> List.collect (function SecOptions o -> o | _ -> [])
              When = pick (function SecWhen w -> Some w | _ -> None)
              Post = defaultArg (pick (function SecPost p -> Some p | _ -> None)) []
              Nested = defaultArg (pick (function SecNested(s, _) -> Some s | _ -> None)) []
              IsParallel = defaultArg (pick (function SecNested(_, p) -> Some p | _ -> None)) false
              FailFast = defaultArg (pick (function SecFailFast f -> Some f | _ -> None)) false
              Position = pos }

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------

type private TopSection =
    | TopAgent of AgentSpec
    | TopEnv of (string * string * bool) list
    | TopTools of (string * string) list
    | TopOptions of Step list
    | TopParameters of Step list
    | TopTriggers of Step list
    | TopStages of Stage list
    | TopPost of (PostCondition * Step list) list
    | TopOther of string

let private topSection: P<TopSection> =
    choice
        [ attempt (agentSpec |>> TopAgent)
          attempt (environmentSection |>> TopEnv)
          attempt (toolsSection |>> TopTools)
          attempt (keyword "options" >>. stepBlock |>> TopOptions)
          attempt (keyword "parameters" >>. stepBlock |>> TopParameters)
          attempt (keyword "triggers" >>. stepBlock |>> TopTriggers)
          attempt (keyword "stages" >>. stagesBody |>> TopStages)
          attempt (postSection |>> TopPost)
          attempt (keyword "libraries" >>. balancedRaw '{' '}' |>> fun _ -> TopOther "libraries")
          // THE TOP-LEVEL FALLBACK MUST NOT CLAIM SECTIONS FOGELL ACTS ON. Same shape
          // as FG-143 one level up: when `options` failed to parse for any reason this
          // recorded the whole block as `TopOther` and every directive inside it was
          // dropped without a word — including the ones the refusal model exists to
          // enforce. A dropped `timeout` is a build that runs past a limit Jenkins
          // applies. `stages` is here for the same reason: a pipeline whose stages do
          // not parse must not be recorded as an opaque section and reported green.
          (identifier
           >>= (fun n ->
               if actedOnSections.Contains n then
                   fail $"a `{n}` section that does not parse is refused, never recorded as an opaque section"
               else
                   (attempt (balancedRaw '{' '}') <|> attempt (balancedRaw '(' ')'))
                   |>> fun _ -> TopOther n)) ]

/// Leading trivia a real Jenkinsfile carries before `pipeline {`: a shebang,
/// `@Library` annotations, `import` lines, and top-level `def`s. All are legal
/// and none belong to the Declarative schema, so they are skipped here rather
/// than rejected.
let private preamble: P<unit> =
    let shebang = attempt (skipString "#!" >>. skipRestOfLine true)
    let annotation =
        attempt (skipString "@" >>. skipMany1Satisfy isIdentCont
                 >>. opt (balancedRaw '(' ')') >>. ws >>. opt (attempt (symbol "_")) >>% ())
    let importLine = attempt (keyword "import" >>. skipRestOfLine true)
    let defLine =
        attempt (
            keyword "def"
            >>. skipMany1Satisfy isIdentCont
            >>. ws
            >>. opt (attempt (balancedRaw '(' ')'))
            >>. ws
            >>. (attempt (balancedRaw '{' '}' >>% ())
                 <|> (opt (symbol "=") >>. skipRestOfLine true)))

    ws >>. skipMany (choice [ shebang; annotation; importLine; defLine ] .>> ws)

/// Skip forward to the `pipeline {` token. Real Jenkinsfiles put helper
/// functions, `@Library` annotations, imports and `properties(...)` calls both
/// before *and* after the declarative block; none of that is part of the
/// Declarative schema. Iterative (skipManyTill), so a long file cannot
/// overflow the stack the way a recursive scan would.
let private skipToPipeline: P<unit> =
    skipManyTill anyChar (lookAhead (attempt (keyword "pipeline" >>. skipChar '{')))

let private pipelineParser: P<Pipeline> =
    // FG-188. The skipped text is CAPTURED, not merely stepped over. Everything before
    // `pipeline {` used to be discarded, which made a top-level `def` helper invisible to
    // every `script { }` body — the commonest escape construct in the corpus.
    //
    // `withSkippedString` reads exactly what `preamble >>. skipToPipeline` consumed, so
    // the capture cannot drift from the skip: there is no second scanner to keep in
    // agreement with this one.
    withSkippedString (fun skipped () -> skipped) (preamble >>. skipToPipeline)
    .>>. (keyword "pipeline"
          >>. between (symbol "{") (symbol "}") (ws >>. many (attempt topSection))
          .>> ws)
    |>> fun (capturedPreamble, sections) ->
            let pick f = sections |> List.tryPick f
            { Agent = defaultArg (pick (function TopAgent a -> Some a | _ -> None)) AgentNone
              Environment =
                defaultArg (pick (function TopEnv e -> Some(e |> List.map (fun (n, v, _) -> n, v)) | _ -> None)) []
              EnvironmentLiteralNames =
                defaultArg
                    (pick (function
                        | TopEnv e -> Some(e |> List.choose (fun (n, _, i) -> if i then None else Some n) |> Set.ofList)
                        | _ -> None))
                    Set.empty
              Tools = defaultArg (pick (function TopTools t -> Some t | _ -> None)) []
            // EVERY `options` SECTION, not the first. `pick` is `tryPick`, so a
            // second `options { }` block was invisible: `options { buildDiscarder(...) }`
            // followed by `options { retry(2) }` parsed as the first alone, and the
            // FG-053 refusal never saw the directive it exists to catch — Fogell ran
            // a build Jenkins rejects. This is the same first-vs-all mistake this
            // file already fixed one level down, where `tryFind` over the ENTRIES of
            // one block accepted `timestamps(); timestamps(false)`; the section level
            // kept it. Collecting them concatenated also preserves the duplicate for
            // the arg validators, which is what makes the repeat detectable at all.
              Options = sections |> List.collect (function TopOptions o -> o | _ -> [])
              Parameters = defaultArg (pick (function TopParameters p -> Some p | _ -> None)) []
              Triggers = defaultArg (pick (function TopTriggers t -> Some t | _ -> None)) []
              Preamble = capturedPreamble
              Stages = defaultArg (pick (function TopStages s -> Some s | _ -> None)) []
              Post = defaultArg (pick (function TopPost p -> Some p | _ -> None)) [] }

/// Does this source look like a Declarative pipeline at all? Deliberately
/// stricter than Forge's bare regex: the token must not be inside a line
/// comment or a string, which Forge's version does not check (FG-012).
let looksDeclarative (source: string) : bool =
    let stripped =
        System.Text.RegularExpressions.Regex.Replace(
            source,
            @"//[^\n]*|/\*.*?\*/|'(?:[^'\\\n]|\\.)*'|""(?:[^""\\\n]|\\.)*""",
            " ",
            System.Text.RegularExpressions.RegexOptions.Singleline)

    System.Text.RegularExpressions.Regex.IsMatch(stripped, @"(^|[\s};])pipeline\s*\{")

/// FG-183. `break` and `continue` are only legal inside a loop, and a CLOSURE IS NOT A
/// LOOP. Jenkins refuses a pipeline containing a misplaced one at COMPILE time, before
/// any stage runs; Fogell admitted it, ran the earlier stages, and then let a
/// `BreakSignal` escape to the top of the engine.
///
/// MEASURED, both engines. `script { dir('d') { break } }` behind a stage that writes a
/// file: Jenkins fails the Groovy compile naming the position and NEVER STARTS A STAGE,
/// so its workspace is empty; Fogell ran the first stage to completion, wrote the file,
/// and ended `run failed: BreakSignal`. Two defects in one input — a durable effect
/// Jenkins cannot produce, and an engine FAULT where ADR 0001 requires a rejection with a
/// named code and a position. An uncaught exception is the one outcome no tier describes.
///
/// NOT A DIFFERENTIAL RECEIPT, and the reason is this rule's own effect: Fogell now
/// REFUSES where Jenkins FAILS A BUILD, and the harness cannot compare a refusal against a
/// build — it scored the attempt NOT-COMPARABLE. Proven instead by proof case
/// `break-outside-loop`, which asserts the refusal AND that no earlier stage ran;
/// that file's pre-existing helper could not tell an admission refusal from a runtime
/// fault, since both leave a diagnostic in the log and only the absent marker separates
/// them.
///
/// WHY NOT CATCH THE SIGNAL AT THE TOP instead, which is the smaller change: that
/// converts the fault into a stage failure and STILL runs the earlier stages. Those
/// effects are what Jenkins never produced, so catching trades a loud wrong answer for a
/// quiet one — the exchange ADR 0001 exists to forbid.
///
/// A PARSER SUCCESS IS NOT A VALIDITY VERDICT. The Groovy parser accepts these keywords
/// unconditionally and is right to: placement is a CONTEXT rule, not a grammar one. So
/// this is a walk of the parsed body rather than a grammar change.
///
/// THE CLOSURE BOUNDARY IS THE SUBTLE PART, and it is why `inLoop` RESETS on the way in
/// rather than being inherited. Groovy rejects `break` inside a closure even when the
/// closure is written inside a loop, because the closure is a separate body — so
/// `while (x) { [1].each { break } }` is invalid, and a check that inherited the
/// enclosing loop would admit it. `SFunc` bodies reset for the same reason.
///
/// THE POSITION IS THE SCRIPT STEP'S, not the keyword's, and that is a stated limit
/// rather than an oversight: this AST carries no positions. Jenkins names a line and
/// column inside the script; Fogell names the `script` block containing it. The existing
/// malformed-body path already reports at exactly that granularity, so the two agree.
module private LoopControl =
    // Scoped to this module rather than opened at the top of the file: the IR and the
    // Groovy AST both define short type names, and a file-wide open would resolve them
    // by import order rather than by intent.
    open Fogell.Groovy

    let misplaced (script: Script) : string list =
        // TWO FLAGS, NOT ONE, because `switch` legalises exactly one of the two keywords.
        // A single `inLoop` could not say "break is fine here, continue is not": passing it
        // as true for an arm body admitted a `continue` that belongs to a loop there isn't
        // one of, and passing it through unchanged refused a `break` the switch itself
        // catches. The language distinguishes them, so the walk has to.
        let rec stmts breakOk continueOk ss =
            ss |> List.collect (stmt breakOk continueOk)

        and stmt breakOk continueOk s =
            match s with
            | SBreak -> if breakOk then [] else [ "break" ]
            | SContinue -> if continueOk then [] else [ "continue" ]
            | SWhile(c, b) -> expr c @ stmts true true b
            | SForIn(_, src, b) -> expr src @ stmts true true b
            // A SWITCH IS A BREAK BOUNDARY — the interpreter catches the signal at the
            // switch, so `break` is legal in any arm at any depth. `continue` is untouched
            // by it and still needs a real enclosing loop. Three defects came from this
            // distinction being unrepresentable while `switch` was lowered to nested ifs;
            // with the node it is one arm that says what the language says.
            | SSwitch(subject, arms) ->
                expr subject
                @ (arms
                   |> List.collect (fun (case_, b) ->
                       (case_ |> Option.map expr |> Option.defaultValue [])
                       @ stmts true continueOk b))
            | SIf(c, a, b) -> expr c @ stmts breakOk continueOk a @ stmts breakOk continueOk b
            | STry(b, catch, fin) ->
                stmts breakOk continueOk b
                @ (catch
                   |> Option.map (fun (_, _, h) -> stmts breakOk continueOk h)
                   |> Option.defaultValue [])
                @ stmts breakOk continueOk fin
            // A function body is its own scope: a loop around the DEFINITION does not make
            // a `break` inside the function legal.
            | SFunc(_, _, b) -> stmts false false b
            | SExpr e -> expr e
            | SDef(_, v) -> v |> Option.map expr |> Option.defaultValue []
            | SAssign(t, v) -> expr t @ expr v
            | SReturn v -> v |> Option.map expr |> Option.defaultValue []
            | SThrow e -> expr e

        // EVERY expression form is walked, and the wildcard is deliberately absent: a
        // closure can be written anywhere a value can, so a `| _ -> []` catch-all would
        // silently stop checking whichever form was added next. FS0025 makes that a build
        // error instead.
        and expr e =
            match e with
            | EClosure c -> stmts false false c.Body
            | ECall(target, args, trailing) ->
                callTarget target
                @ (args
                   |> List.collect (function
                       | APos v -> expr v
                       | ANamed(_, v) -> expr v))
                @ (trailing |> Option.map (fun c -> stmts false false c.Body) |> Option.defaultValue [])
            | EGString parts ->
                parts
                |> List.collect (function
                    | GLit _ -> []
                    | GExpr x -> expr x)
            | EList xs -> xs |> List.collect expr
            | EMap kvs -> kvs |> List.collect (snd >> expr)
            | EProp(t, _)
            | ESafeProp(t, _) -> expr t
            | EIndex(t, i) -> expr t @ expr i
            | EUnary(_, o) -> expr o
            | EBinary(_, l, r) -> expr l @ expr r
            | ETernary(c, a, b) -> expr c @ expr a @ expr b
            | EElvis(a, b) -> expr a @ expr b
            | ENull
            | EBool _
            | EInt _
            | EStr _
            | EVar _ -> []

        and callTarget t =
            match t with
            | FreeCall _ -> []
            | MethodCall(target, _)
            | SafeMethodCall(target, _) -> expr target

        stmts false false script

/// FG-178. Every `script { }` body in the model, including nested stages, nested step
/// blocks and both `post` levels.
///
/// THIRD ENUMERATION OF THE SAME THING, which is why it is finally here instead of at a
/// caller. It began as a walk of `stage.Steps`; review found `post` blocks; review then
/// found the HTTP admission endpoint, which calls `parse` DIRECTLY and so reached none of
/// it — a submission with an invalid body was accepted and queued, failing only when a
/// runner picked it up, where the contract promises a 422 at admission.
///
/// Putting it inside `parse` is what makes "which entry points are covered" stop being a
/// question: there is one.
let private scriptBodyErrors (pipeline: Pipeline) : (string * Position) list =
    let rec fromSteps (steps: Step list) =
        steps
        |> List.collect (fun st ->
            let here =
                match st.ScriptBody with
                | Some src ->
                    match Fogell.Groovy.Parser.Parser.parse src with
                    | Result.Error e -> [ $"script block did not parse as Groovy: {string e}", st.Position ]
                    | Result.Ok parsed ->
                        // FG-183. Parsed, and still not admissible.
                        match LoopControl.misplaced parsed with
                        | keyword :: _ ->
                            // NAMES THE ONE THING THIS CHECK KNOWS. It briefly hedged
                            // across two readings — "not inside a loop, and not a switch
                            // arm's final statement" — because a lowered `switch` could
                            // still deliver an arm's `break` here, and Jenkins ACCEPTS
                            // that one, so claiming a compile rejection would have been
                            // false. It is true now for a DIFFERENT reason than that hedge
                            // recorded, and the stale rationale outlived the lowering that
                            // made it necessary: `LoopControl.misplaced` accounts for
                            // switch break boundaries itself, so an arm's `break` is legal
                            // at any depth and never arrives here, while a `continue` with
                            // no enclosing loop still does. A refusal that misreports its
                            // reason sends an author to fix the wrong thing, so the reason
                            // has to move whenever the rule does.
                            [ $"script block: `{keyword}` outside a loop; Jenkins rejects the pipeline at compile time",
                              st.Position ]
                        | [] -> []
                | None -> []

            here @ fromSteps st.Block)

    // FG-175. A `when { expression { … } }` BODY IS GROOVY TOO, and it was admitted
    // unparsed. Jenkins compiles the whole file before starting anything, so a malformed
    // condition means NO stage runs; Fogell admitted the pipeline, ran the earlier stages,
    // and then skipped the gated one when its condition would not evaluate — MEASURED:
    // Jenkins' workspace is EMPTY and Fogell's holds `early.txt`. Proof case
    // `when-malformed-expression`, which asserts the refusal AND that no stage ran. A durable effect Jenkins
    // cannot produce, which is the ADR 0001 class rather than a stage-selection quirk.
    //
    // It rides in `scriptBodyErrors` deliberately: that function is the one place every
    // entry point routes through (FG-185 moved it into `parseWithLimits` for exactly this
    // reason), so the check cannot be inherited by some callers and not others — which is
    // the partial-enumeration shape this branch has met at four different layers.
    //
    // THE POSITION IS THE STAGE'S FIRST STEP, or 1:1 for a stage with none. `WhenCondition`
    // carries source text but no position, and inventing a more precise one would be a
    // claim the IR cannot support.
    let rec whenSources (c: WhenCondition) =
        match c with
        | WhenExpression src -> [ src ]
        | WhenAllOf cs
        | WhenAnyOf cs -> cs |> List.collect whenSources
        | WhenNot inner -> whenSources inner
        | _ -> []

    let whenErrors (stage: Stage) =
        match stage.When with
        | None -> []
        | Some cond ->
            whenSources cond
            |> List.collect (fun src ->
                match Fogell.Groovy.Parser.Parser.parse src with
                | Result.Error e ->
                    let position =
                        match stage.Steps with
                        | first :: _ -> first.Position
                        | [] -> { Line = 1L; Column = 1L }

                    [ $"when expression did not parse as Groovy: {string e}", position ]
                | Result.Ok _ -> [])

    let stages = Pipeline.flattenStages pipeline.Stages

    (stages |> List.collect (fun stage -> fromSteps stage.Steps))
    @ (stages |> List.collect (fun stage -> stage.Post |> List.collect (snd >> fromSteps)))
    @ (pipeline.Post |> List.collect (snd >> fromSteps))
    @ (stages |> List.collect whenErrors)

/// Parse a Declarative Jenkinsfile. Admission limits are applied first so a
/// hostile input never reaches the recursive grammar.
///
/// FG-185. SCRIPT-BODY VALIDATION LIVES HERE, not in `parse`. It was in `parse`, under a
/// comment claiming that "which entry points are covered" had stopped being a question
/// because there was only one — and `parseWithLimits` was a second, public, and exempt.
/// A caller choosing custom limits got `Ok` for a pipeline the default entry point
/// rejects, which restores the delayed execution-time failure the check exists to prevent
/// for anyone who passes limits. FOURTH ENUMERATION OF THE SAME RULE (stage steps, then
/// `post`, then the HTTP endpoint, now custom limits), and it is this branch's recurring
/// shape: a rule covering one path while an equivalent path stays open. `parse` now only
/// supplies the defaults, so there is nothing left to forget. Raised in review on PR #53.
let parseWithLimits (limits: Limits) (source: string) : Result<Pipeline, AdmissionError> =
    match Limits.precheck limits source with
    | Result.Error e -> Result.Error e
    | Result.Ok() ->
        if not (looksDeclarative source) then
            Result.Error(AdmissionError.at NoPipelineBlock 1L 1L "no declarative `pipeline { }` block found")
        else
            match runParserOnString (pipelineParser .>> ws) () "Jenkinsfile" source with
            | ParserResult.Success(p, _, _) ->
                if List.isEmpty p.Stages then
                    Result.Error(AdmissionError.at NoStages 1L 1L "pipeline declares no stages")
                else
                    match scriptBodyErrors p with
                    | (why, position) :: _ ->
                        Result.Error
                            { Code = MalformedSyntax
                              Message = why
                              Position = position }
                    | [] -> Result.Ok p
            | ParserResult.Failure(msg, err, _) ->
                let pos = err.Position

                let firstLine =
                    msg.Split('\n')
                    |> Array.filter (fun l -> l.Trim() <> "")
                    |> Array.tryLast
                    |> Option.defaultValue "unparsable"

                Result.Error(AdmissionError.at MalformedSyntax pos.Line pos.Column (firstLine.Trim()))

let parse (source: string) : Result<Pipeline, AdmissionError> = parseWithLimits Limits.defaults source



