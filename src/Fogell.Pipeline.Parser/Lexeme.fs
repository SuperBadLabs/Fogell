module Fogell.Pipeline.Parser.Lexeme

open FParsec
open Fogell.Ir

/// Shared lexing for the Declarative grammar.
///
/// Design note, learned from measuring Forge: a Declarative Jenkinsfile is
/// Groovy, so the lexer must be Groovy-aware about the things that can *contain*
/// braces and quotes — strings, GStrings, slashy strings, comments — or the
/// block matcher counts a `{` inside a comment and the whole parse derails.

type P<'a> = Parser<'a, unit>

let ws1: P<unit> = skipMany1 (anyOf " \t\r\n")

let lineComment: P<unit> = skipString "//" >>. skipRestOfLine false

let blockComment: P<unit> =
    skipString "/*" >>. skipCharsTillString "*/" true System.Int32.MaxValue

/// Whitespace plus both comment forms. Declarative bodies are newline
/// separated, so newlines are ordinary whitespace here.
let ws: P<unit> = skipMany (choice [ ws1; lineComment; blockComment ])

let lexeme (p: P<'a>) : P<'a> = p .>> ws
let symbol (s: string) : P<unit> = lexeme (skipString s)
let keyword (s: string) : P<unit> =
    lexeme (attempt (skipString s .>> notFollowedBy (satisfy (fun c -> isLetter c || isDigit c || c = '_'))))

let isIdentStart c = isLetter c || c = '_'
let isIdentCont c = isLetter c || isDigit c || c = '_'

let identifier: P<string> = lexeme (many1Satisfy2 isIdentStart isIdentCont)

let position: P<Position> =
    getPosition |>> fun p -> { Line = p.Line; Column = p.Column }

// --- string literals -------------------------------------------------------
// Value only; interpolation is NOT evaluated here. ADR 0002: the parser
// records source, the interpreter decides meaning.

/// FG-122. In the QUOTED forms — single, double, and their triple variants —
/// Groovy's string escapes are Java's: the simple letters, a UNICODE escape,
/// and an OCTAL escape of one or two digits, or three when the first is 0-3
/// (see the range note below; a flat "one to three" is what this comment said
/// while the code was greedy, and both were wrong together).
///
/// NOT slashy strings, which this comment covered until the pre-push verifier's
/// model review on PR #36. Those escape only their delimiter — see
/// [slashyQuoted], and FG-125 for what routing them through here cost.
///
/// MEASURED against Jenkins 2.568.1: `sh 'printf "\\033[31mred\\033[0m"'` traced
/// as `+ printf red` there — a real ESC byte, which normalisation then strips —
/// and as `+ printf 033[31mred033[0m` here (receipt `sh-octal-escape`), because the fallback returned the
/// escape's first character and left `33` as ordinary text. The two engines ran
/// DIFFERENT COMMANDS for the same Jenkinsfile, and every single-quoted `sh`
/// body carrying a backslash escape diverged, not only colour.
///
/// SHARED by both escape parsers below. The simple-letter map existed twice —
/// here and in `escapedCharKeepingDollar` — and adding octal to one would have
/// left the other reading `\033` as three characters, which is the drift this
/// project has spent a branch learning to prevent by deleting the copy.
/// NUL and SOH are PARSER PROVENANCE MARKERS, not ordinary characters: NUL means
/// "this dollar was escaped" (restored to `$` downstream) and a leading SOH means
/// "this argument was an unquoted expression" (`Parser.fs`). Before FG-122 no
/// escape could produce either — the old fallback dropped the backslash and left
/// digits as text — so the comment claiming "NUL cannot occur ... it cannot
/// collide" was true when written and MY OWN numeric decoding invalidated it:
/// `"\000X"` rendered `$X`, and a value starting `\001` was evaluated as an
/// expression instead of forwarded.
///
/// Refusing to decode into a sentinel makes the escape fall through to the
/// literal fallback, which is exactly the pre-FG-122 behaviour for these two
/// code points — no worse than before for them, correct for everything else, and
/// incapable of corrupting a value. Out-of-band provenance is the real fix:
/// FG-127. Raised by BOTH reviewers on PR #36.
let private rejectSentinel (c: char) : P<char> =
    if c = '\u0000' || c = '\u0001' then
        fail "escape decodes to a parser provenance sentinel"
    else
        preturn c

let private numericEscape: P<char> =
  attempt (
    // ONE OR MORE `u`: Java's UnicodeEscape is `\ u+ HexDigit{4}`, so `\uu0041`
    // is also `A`. Accepting exactly one passed `uu0041` through as text while
    // the board row claimed unicode escapes were handled — an overclaim I wrote.
    // Raised by the pre-push verifier's model review on PR #36.
    (skipMany1 (skipChar 'u')
     >>. manyMinMaxSatisfy 4 4 (fun c ->
         (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
     |>> fun hex -> char (System.Convert.ToInt32(hex, 16)))
    // THREE digits only when the first is 0-3 — Java's OctalEscape production is
    // `ZeroToThree OctalDigit OctalDigit`, so `\400` is the TWO-digit `\40` (a
    // space) followed by a literal `0`. MEASURED on Jenkins 2.568.1, receipt
    // `sh-escape-edges`: `printf "[\400]"` traces `+ printf [ 0]` there, where a
    // greedy 1-3 reader yielded `[Ā]` (U+0100) and the case DIVERGED.
    // Raised by Codex on PR #36.
    <|> attempt (
            manyMinMaxSatisfy 1 1 (fun c -> c >= '0' && c <= '3')
            .>>. manyMinMaxSatisfy 2 2 (fun c -> c >= '0' && c <= '7')
            |>> fun (hi, lo) -> char (System.Convert.ToInt32(hi + lo, 8)))
    <|> (manyMinMaxSatisfy 1 2 (fun c -> c >= '0' && c <= '7')
         |>> fun digits -> char (System.Convert.ToInt32(digits, 8)))
    >>= rejectSentinel)

let private simpleEscape (c: char) =
    match c with
    | 'n' -> '\n'
    | 't' -> '\t'
    | 'r' -> '\r'
    | 'b' -> '\b'
    // '\f', not '\012': in F# that trigraph is DECIMAL, so it reads as octal 12
    // to anyone carrying Java's escapes in their head — which is everyone
    // touching this function. Raised by Copilot on PR #36.
    | 'f' -> '\f'
    | c -> c

let private escapedChar: P<char> =
    skipChar '\\' >>. (numericEscape <|> (anyChar |>> simpleEscape))

/// The ONLY thing that separates [escapedCharKeepingDollar] from [escapedChar].
///
/// A NUL sentinel, not "\$": REVIEW FIX (Codex, PR #14 round 9). `"\\$X"` is an
/// escaped BACKSLASH followed by a live interpolation — Groovy yields one
/// backslash and expands `$X`. Decoding the escaped dollar to "\$" made the two
/// cases indistinguishable downstream, so that value came out as a literal `$X`.
/// NUL cannot occur in an environment value, so it cannot collide.
///
/// It applies to a NUMERIC escape too: `\044` decodes to `$`, and Groovy decides
/// interpolation LEXICALLY — before escapes are decoded — so that dollar is
/// ordinary text and `"\044MISSING"` stays `$MISSING`. Returning a bare "$"
/// handed the GString renderer a live interpolation: MEASURED on Jenkins
/// 2.568.1, receipt `sh-escape-edges` — jenkins=success, fogell=FAILURE on the
/// unresolvable name, a build Jenkins passes. Raised by Codex on PR #36.
let private keepDollar (c: char) : string =
    if c = '$' then "\u0000" else string c

let private quoted (q: string) : P<string> =
    between (skipString q) (skipString q) (
        manyChars (escapedChar <|> satisfy (fun c -> c <> q.[0] && c <> '\n')))

let private tripleQuoted (q: string) : P<string> =
    between (skipString q) (skipString q) (
        manyCharsTill (escapedChar <|> anyChar) (lookAhead (skipString q)))

/// FG-125. A SLASHY string is the one form whose escapes are NOT Java's: it
/// escapes only its `/` delimiter and preserves every other backslash sequence
/// verbatim.
///
/// MEASURED on Jenkins 2.568.1, receipt `sh-slashy-escape`:
/// `sh(/printf '[\033]' > slashy.txt/)` traces `+ printf [\033]` there — a
/// backslash and three digits. Routing slashy through `escapedChar` decoded an
/// ESC byte and Fogell traced `+ printf []`, so the two engines ran DIFFERENT
/// COMMANDS — the same defect FG-122 fixed for the quoted forms while leaving
/// it standing here, because all four slashy call sites called `quoted "/"`.
///
/// The parenthesised call is what made this measurable at all: `sh /.../` with
/// no parentheses is REFUSED by Jenkins at compile time, and the probe that used
/// it proved nothing while looking like evidence.
let private slashyQuoted: P<string> =
    between (skipString "/") (skipString "/") (
        manyStrings (
            (skipChar '\\' >>. (anyChar |>> fun c -> if c = '/' then "/" else "\\" + string c))
            <|> (satisfy (fun c -> c <> '/' && c <> '\n') |>> string)))

/// As [escapedChar], but an escaped $ keeps its backslash.
///
/// REVIEW FIX (Codex, PR #14 round 7): in a GString, "\$BUILD_NUMBER" is the
/// literal text $BUILD_NUMBER — Groovy does not interpolate it. The backslash
/// was stripped here, BEFORE the value was classified as interpolating, so the
/// interpolation pass expanded it and the step ran with a different environment than
/// Jenkins. The marker now survives to the interpolation pass, which honours it and
/// then removes it.
let private escapedCharKeepingDollar: P<string> =
    skipChar '\\'
    >>. ((numericEscape |>> keepDollar) <|> (anyChar |>> (simpleEscape >> keepDollar)))

/// Variants used where interpolation provenance matters, so \$ is preserved.
let private quotedKeepingDollar (q: string) : P<string> =
    between (skipString q) (skipString q) (
        manyStrings (escapedCharKeepingDollar <|> (satisfy (fun c -> c <> q.[0] && c <> '\n') |>> string)))

let private tripleQuotedKeepingDollar (q: string) : P<string> =
    between (skipString q) (skipString q) (
        manyTill (escapedCharKeepingDollar <|> (anyChar |>> string)) (lookAhead (skipString q))
        |>> String.concat "")

/// Any Groovy string form Jenkinsfiles use, including slashy strings.
/// A string literal PLUS whether Groovy would interpolate it.
///
/// REVIEW FIX (Codex, PR #14 round 4): quote type was discarded, so a
/// single-quoted `LITERAL = '$BUILD_NUMBER'` — which Groovy keeps VERBATIM —
/// was interpolated anyway. That runs a stage or command with a different value
/// than Jenkins and can produce a FALSE differential match, the one outcome this
/// harness must never produce. Measured 53 single-quoted dollar-bearing
/// assignments in the 228-file corpus, so it is not hypothetical.
///
/// Single-quoted forms are literal. Double-quoted, triple-double AND slashy are
/// GStrings and interpolate.
let stringLiteralWithKind: P<string * bool> =
    lexeme (
        choice
            [ attempt (tripleQuoted "'''" |>> fun s -> s, false)
              attempt (tripleQuotedKeepingDollar "\"\"\"" |>> fun s -> s, true)
              attempt (quoted "'" |>> fun s -> s, false)
              attempt (quotedKeepingDollar "\"" |>> fun s -> s, true)
              // REVIEW FIX (Codex, PR #14 round 5): a slashy string is a GString in
              // Groovy and DOES interpolate, so `IMAGE = /build-$BUILD_NUMBER/` must
              // expand. Only single-quoted forms are literal.
              attempt (slashyQuoted |>> fun s -> s, true) ])

/// Quote KIND without the NUL sentinel, for consumers that forward the value verbatim
/// instead of interpolating it.
///
/// REVIEW FIX (Codex, PR #17 round 4): using [stringLiteralWithKind] for every step's
/// named arguments put the sentinel into values that nothing restores — only the `input`
/// rendering path calls `interpolate` — so `sh(script: "echo \$BUILD_NUMBER")` handed
/// the shell an embedded NUL. The sentinel exists solely to survive interpolation, so it
/// must not be written where interpolation never happens.
/// Both forms of a string literal at once: the PLAIN value (escaped dollars already
/// collapsed, safe to forward verbatim) and the ESCAPE-PRESERVING value (NUL sentinel,
/// safe only for a consumer that interpolates), plus whether it is a GString.
///
/// FG-046. `input` renders its own prompt, so it must show a literal \$TARGET while a
/// shell step must never see the sentinel. Producing both at the parse site is the only
/// place the distinction is still available.
let stringLiteralWithKindBoth: P<string * string * bool> =
    lexeme (
        choice
            [ attempt (tripleQuoted "'''" |>> fun s -> s, s, false)
              attempt (tripleQuotedKeepingDollar "\"\"\"" |>> fun s -> s.Replace("\u0000", "$"), s, true)
              attempt (quoted "'" |>> fun s -> s, s, false)
              attempt (quotedKeepingDollar "\"" |>> fun s -> s.Replace("\u0000", "$"), s, true)
              attempt (slashyQuoted |>> fun s -> s, s, true) ])

let stringLiteralWithKindPlain: P<string * bool> =
    lexeme (
        choice
            [ attempt (tripleQuoted "'''" |>> fun s -> s, false)
              attempt (tripleQuoted "\"\"\"" |>> fun s -> s, true)
              attempt (quoted "'" |>> fun s -> s, false)
              attempt (quoted "\"" |>> fun s -> s, true)
              attempt (slashyQuoted |>> fun s -> s, true) ])

let stringLiteral: P<string> =
    lexeme (
        choice
            [ attempt (tripleQuoted "'''")
              attempt (tripleQuoted "\"\"\"")
              attempt (quoted "'")
              attempt (quoted "\"")
              attempt slashyQuoted ])

/// The RAW SOURCE of a Groovy string literal — the FIVE forms this lexer knows
/// (triple-single, triple-double, single, double, slashy), escapes respected.
/// NOT dollar-slashy `$/.../$`, which nothing here parses; a `;` inside one is
/// unprotected. "every form" is what this said, and it was never true.
///
/// FG-138. `Lexeme` is where this codebase knows what a Groovy string IS —
/// [stringLiteral] above enumerates triple-single, triple-double, single, double
/// and slashy. [balancedRaw] below skips `'`, `"`, comments AND — since FG-141
/// landed 2026-08-18 — position-decided slashy spans. The gap this paragraph
/// used to document as OPEN: a slashy carrying the active closing delimiter,
/// such as `/a}b/` inside a `when { expression { … } }`, was counted as a brace
/// and ended the balanced region early (measured then against the host as
/// `no_stages: pipeline declares no stages`, the whole structure collapsed).
/// PROVEN NOW by receipts `when-slashy-brace` and `script-slashy-brace`, the
/// receipts this note promised would arrive with the fix. FG-140. `Parser` needed the same knowledge to
/// stop a raw argument at a `;` OUTSIDE a literal, and grew its own character
/// scanner instead. Five review rounds found five forms it had missed, and two
/// intermediate states produced SILENT no-ops — a build exiting 0 with no
/// `step-started` and no files.
///
/// Three scanners disagreeing about what a string is was the defect; the
/// semicolons only exposed it. This is the one implementation, here, where the
/// other two already live.
///
/// Returns the literal's SOURCE including delimiters, because a caller
/// reassembling a raw expression needs the text back exactly as written —
/// [stringLiteral] decodes, which is the wrong thing for that job.
let stringSpanRaw: P<string> =
    let scan (stream: CharStream<unit>) =
        let start = stream.Index
        let c = stream.Peek()

        let readDelimited (q: char) (tripled: bool) =
            let closer = if tripled then System.String(q, 3) else string q
            for _ in 1 .. closer.Length do stream.Skip()
            let mutable closed = false
            let mutable unterminated = false

            while not closed && not stream.IsEndOfStream do
                let d = stream.Peek()

                if d = '\\' then
                    stream.Skip()
                    if not stream.IsEndOfStream then stream.Skip()
                elif not tripled && d = q then
                    stream.Skip()
                    closed <- true
                elif tripled && stream.PeekString 3 = closer then
                    for _ in 1 .. 3 do stream.Skip()
                    closed <- true
                elif d = '\n' && not tripled && q <> '/' then
                    // AN UNTERMINATED SINGLE-LINE LITERAL IS AN ERROR, not a span.
                    // This used to report `closed`, so the caller received raw source
                    // MISSING ITS CLOSING DELIMITER while the contract above promises
                    // the delimiters are included. A malformed literal was then partly
                    // accepted instead of failing closed — the same preference for
                    // guessing over refusing that FG-143/145/147 each had to remove.
                    unterminated <- true
                    closed <- true
                else
                    stream.Skip()

            closed && not unterminated

        if c = '\'' || c = '"' then
            let tripled = stream.PeekString 3 = System.String(c, 3)
            if readDelimited c tripled then
                let len = int (stream.Index - start)
                stream.Seek start
                Reply(stream.Read len)
            else
                stream.Seek start
                Reply(Error, expected "a terminated string literal")
        elif c = '/' then
            // SLASHY, ASSUMED NOT DIVISION. This scanner treats any `/` as a slashy
            // opener and does NOT decide between a literal and a division operator.
            //
            // NO CALLER SENDS `/` HERE ANY MORE. `rawArgValue` reaches this only
            // after `'` or `"`; it once excluded `/` from plain text and tried
            // here, and that was an APPROVAL BYPASS — `input message: 10 / 2` found
            // no closing delimiter, the argument failed to parse, `steps` backtracked
            // to EMPTY, and the build reported success with no prompt published
            // (FG-141, approval-lane scenario W). This comment described that
            // removed caller path for a round after it was gone.
            if readDelimited '/' false then
                let len = int (stream.Index - start)
                stream.Seek start
                Reply(stream.Read len)
            else
                stream.Seek start
                Reply(Error, expected "a terminated slashy string")
        else
            Reply(Error, expected "a string literal")

    scan

// --- balanced raw capture --------------------------------------------------

/// FG-141. Can the character ending the text so far END an expression? This is
/// the context the slashy-versus-division decision needs, and it is a fact a
/// linear scanner CAN carry: after an identifier character, a closing bracket
/// or a string, a `/` is division; anywhere else — after `=`, `(`, `,`, `:`,
/// an operator, or at the start — Groovy has no left operand, so a `/` can
/// only open a slashy literal. The over-broad fix this ticket twice rejected
/// treated every `/` as an opener and swallowed `10 / 2`; the position test is
/// what makes the narrow claim safe. `}` counts as an ender on purpose: a
/// closure literal is an operand, and mis-deciding division there reproduces
/// the OLD behaviour (slashy unprotected) rather than a new wrong one.
let endsExpression (c: char) =
    isLetter c || isDigit c || c = '_' || c = ')' || c = ']' || c = '}' || c = '\'' || c = '"'

/// Capture the raw source of a balanced region, skipping over strings and
/// comments so their delimiters never affect the depth count. Used for
/// argument lists and for `expression { }` bodies that the interpreter owns.
///
/// FG-141. Slashy spans are skipped too, when the position says slashy (see
/// [endsExpression]): `def pattern = /}/` inside a `script { }` body counted
/// the brace and ended the block early, rejecting the whole pipeline as
/// `opaque section` — a false refusal of valid Groovy. An unterminated
/// candidate span falls back to the ordinary single character, which is
/// exactly the pre-FG-141 reading — the fix can only remove derailments.
let balancedRaw (opening: char) (closing: char) : P<string> =
    let inner (stream: CharStream<unit>) =
        if stream.Peek() <> opening then
            Reply(Error, expected $"'{opening}'")
        else
            let start = stream.Index
            stream.Skip()
            let mutable depth = 1
            let mutable failed = false
            // the region opener starts an expression context; a `/` first thing
            // in the body is a slashy opener
            let mutable lastSig = ' '

            while depth > 0 && not failed && not (stream.IsEndOfStream) do
                let c = stream.Peek()

                if c = '\'' || c = '"' then
                    // TRIPLE-QUOTED SPANS FIRST. Treating `"""` as an empty string
                    // followed by raw text made every delimiter inside a triple-quoted
                    // value count towards depth: `input(message: """Deploy " )?""",
                    // ok: "Ship it")` — valid Jenkins — reported `unbalanced '('`. Before
                    // FG-147 that produced a WRONG PROMPT; after it, a REFUSAL of a
                    // legitimate pipeline, which is how fail-closed turns a silent bug
                    // into a visible one. The docs said this function skipped every
                    // Groovy string form; it skipped one character to the next matching
                    // one. MEASURED, approval-lane scenario Z4.
                    let q = c
                    let isTriple = stream.Peek(1) = q && stream.Peek(2) = q

                    if isTriple then
                        stream.Skip(3)

                        let mutable closed = false

                        while not closed && not stream.IsEndOfStream do
                            let d = stream.Peek()

                            if d = '\\' then
                                stream.Skip()
                                if not stream.IsEndOfStream then stream.Skip()
                            elif d = q && stream.Peek(1) = q && stream.Peek(2) = q then
                                stream.Skip(3)
                                closed <- true
                            else
                                stream.Skip()

                        if not closed then failed <- true
                        lastSig <- q // a completed literal ends an expression
                    else

                    stream.Skip()

                    let mutable closed = false

                    while not closed && not stream.IsEndOfStream do
                        let d = stream.Peek()

                        if d = '\\' then
                            stream.Skip()
                            if not stream.IsEndOfStream then stream.Skip()
                        elif d = q then
                            stream.Skip()
                            closed <- true
                        elif d = '\n' && q = '\'' then
                            closed <- true // unterminated single-quote: bail
                        else
                            stream.Skip()

                    lastSig <- q // a completed literal ends an expression
                elif c = '/' && stream.Peek(1) = '/' then
                    while not stream.IsEndOfStream && stream.Peek() <> '\n' do
                        stream.Skip()
                elif c = '/' && stream.Peek(1) = '*' then
                    stream.Skip()
                    stream.Skip()

                    let mutable ended = false

                    while not ended && not stream.IsEndOfStream do
                        if stream.Peek() = '*' && stream.Peek(1) = '/' then
                            stream.Skip()
                            stream.Skip()
                            ended <- true
                        else
                            stream.Skip()
                elif c = '/' && not (endsExpression lastSig) then
                    // FG-141: no left operand, so this `/` can only open a
                    // slashy. Skip its span; on no closer, rewind and read the
                    // `/` as the ordinary character it always was.
                    let before = stream.Index
                    stream.Skip()
                    let mutable closed = false

                    while not closed && not stream.IsEndOfStream do
                        let d = stream.Peek()

                        if d = '\\' then
                            stream.Skip()
                            if not stream.IsEndOfStream then stream.Skip()
                        elif d = '/' then
                            stream.Skip()
                            closed <- true
                        else
                            stream.Skip()

                    if closed then
                        lastSig <- '\'' // a completed literal ends an expression
                    else
                        stream.Seek(before)
                        stream.Skip()
                        lastSig <- '/'
                else
                    if c = opening then depth <- depth + 1
                    elif c = closing then depth <- depth - 1
                    stream.Skip()

                    if c <> ' ' && c <> '\t' && c <> '\r' && c <> '\n' then
                        lastSig <- c

            if depth <> 0 then
                Reply(Error, messageError $"unbalanced '{opening}'")
            else
                let len = int (stream.Index - start)
                stream.Seek(start)
                let text = stream.Read(len)
                Reply(text)

    inner .>> ws

/// The inside of a balanced region, delimiters stripped.
let balancedBody (opening: char) (closing: char) : P<string> =
    balancedRaw opening closing
    |>> fun raw -> if raw.Length >= 2 then raw.Substring(1, raw.Length - 2) else ""
