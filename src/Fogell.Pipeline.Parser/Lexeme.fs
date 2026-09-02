module Fogell.Pipeline.Parser.Lexeme

open FParsec
open Fogell.Admission
open Fogell.Ir

/// Shared lexing for the Declarative grammar.
///
/// Design note, learned from measuring Forge: a Declarative Jenkinsfile is
/// Groovy, so the lexer must be Groovy-aware about the things that can *contain*
/// braces and quotes — strings, GStrings, slashy strings, comments — or the
/// block matcher counts a `{` inside a comment and the whole parse derails.

/// Semantic refusals are different from ordinary grammar misses. FParsec's
/// `attempt` correctly rewinds the latter so a deliberate opaque fallback can
/// fail closed, but the former must survive that rewind and reach admission.
/// The semantic-refusal cell is intentional: stream backtracking restores the
/// user-state value, not mutations made through the same referenced object.
/// ScalarRefusal is deliberately the opposite: an overlong slashy discovered
/// on an abandoned grammar branch must rewind with that branch, so only the
/// scalar interpretation the parser actually commits can refuse admission.
type ParserState =
    { Refusal: (string * Fogell.Ir.Position) option ref
      Limits: Limits
      ScalarRefusal: AdmissionError option }

let parserStateWithLimits (limits: Limits) =
    { Refusal = ref None
      Limits = limits
      ScalarRefusal = None }

let parserState () = parserStateWithLimits Limits.defaults

type P<'a> = Parser<'a, ParserState>

/// Record a scalar refusal at the point its grammar/scanner has committed to a
/// literal span. Keeping this state immutable is load-bearing: an enclosing
/// `attempt` must rewind a scalar interpretation that the grammar abandons.
let recordScalarContent (rawContent: string) (stream: CharStream<ParserState>) =
    let scalarBytes = System.Text.Encoding.UTF8.GetByteCount rawContent

    if scalarBytes > stream.UserState.Limits.MaxScalarBytes then
        let position = stream.Position

        let refusal =
            AdmissionError.at
                ScalarTooLong
                position.Line
                position.Column
                $"string literal exceeds {stream.UserState.Limits.MaxScalarBytes} UTF-8 bytes"

        stream.UserState <- { stream.UserState with ScalarRefusal = Some refusal }

/// Record a semantic refusal before failing the current parser branch. The
/// fallback may still parse, but admission reads this cell before returning it.
let refuse (message: string) : P<'a> =
    getPosition .>>. getUserState
    >>= fun (position, state) ->
            if state.Refusal.Value.IsNone then
                state.Refusal.Value <-
                    Some(
                        message,
                        { Line = position.Line
                          Column = position.Column }
                    )

            fail message

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

let identifierBare: P<string> = many1Satisfy2 isIdentStart isIdentCont
let identifier: P<string> = lexeme identifierBare

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

/// FG-126a. Jenkins 2.568.1 refuses `\8` while compiling a quoted Groovy
/// literal; the former catch-all dropped the backslash and ran the resulting
/// command. This is deliberately the ONE measured spelling, not a claim that
/// every invalid Groovy escape is classified here. In particular, `\9`,
/// arbitrary letters, provenance sentinels, dollar-slashy text and opaque raw
/// expressions remain outside this tranche.
///
/// `refuse`, rather than an ordinary parser failure, is load-bearing. A whole
/// literal is attempted before the raw-argument fallback, so a plain `fail`
/// would rewind and let that fallback silently admit the original source.
let private measuredInvalidEight: P<char> =
    lookAhead (skipChar '8')
    >>. refuse "invalid Groovy escape `\\8`: `8` is not an octal digit"

/// The one post-backslash operation for both ordinary strings and GStrings.
/// Keeping the refusal between numeric decoding and the historical catch-all
/// makes valid octal escapes win while preventing the fallback from eating 8.
let private decodedEscape: P<char> =
    numericEscape <|> measuredInvalidEight <|> (anyChar |>> simpleEscape)

let private escapedChar: P<char> =
    skipChar '\\' >>. decodedEscape

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
    let content =
        manyStrings (
            (attempt (skipString "\\/" >>% "/"))
            <|> (satisfy (fun c -> c <> '/' && c <> '\n') |>> string))

    let captured =
        between
            (skipString "/")
            (skipString "/")
            (withSkippedString (fun skipped decoded -> decoded, skipped) content)

    // Slashy-versus-division is decided by the surrounding grammar. Capturing
    // the raw content here applies the caller's byte cap only after that
    // decision, without decoding escapes or guessing in Limits.precheck.
    captured .>>. getPosition .>>. getUserState
    >>= fun (((decoded, raw), position), state) ->
            let scalarBytes = System.Text.Encoding.UTF8.GetByteCount raw

            if scalarBytes > state.Limits.MaxScalarBytes then
                let refusal =
                    AdmissionError.at
                        ScalarTooLong
                        position.Line
                        position.Column
                        $"string literal exceeds {state.Limits.MaxScalarBytes} UTF-8 bytes"

                setUserState { state with ScalarRefusal = Some refusal } >>% decoded
            else
                preturn decoded

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
    >>. (decodedEscape |>> keepDollar)

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

/// A decoded string literal that leaves following whitespace in the stream.
/// Most grammar sites want [stringLiteral]; constructs whose statement boundary
/// is a newline need to inspect that trivia before deciding whether another item
/// may follow.
let stringLiteralBare: P<string> =
    choice
        [ attempt (tripleQuoted "'''")
          attempt (tripleQuoted "\"\"\"")
          attempt (quoted "'")
          attempt (quoted "\"")
          attempt slashyQuoted ]

let stringLiteral: P<string> = lexeme stringLiteralBare

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
    let inner (stream: CharStream<ParserState>) =
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
            let mutable priorSig = ' '
            let mutable thirdSig = ' '
            let mutable lastSigIndex = -1L
            let mutable priorSigIndex = -1L

            let recordSignificant c index =
                thirdSig <- priorSig
                priorSig <- lastSig
                priorSigIndex <- lastSigIndex
                lastSig <- c
                lastSigIndex <- index

            let skipEscapedCharacter () =
                stream.Skip()

                if not stream.IsEndOfStream then
                    if stream.Peek() = '\r' then
                        stream.Skip()
                        if not stream.IsEndOfStream && stream.Peek() = '\n' then stream.Skip()
                    else
                        stream.Skip()

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
                                skipEscapedCharacter ()
                            elif d = q && stream.Peek(1) = q && stream.Peek(2) = q then
                                stream.Skip(3)
                                closed <- true
                            else
                                stream.Skip()

                        if not closed then failed <- true
                        recordSignificant q (stream.Index - 1L) // a completed literal ends an expression
                    else

                    stream.Skip()

                    let mutable closed = false

                    while not closed && not failed && not stream.IsEndOfStream do
                        let d = stream.Peek()

                        if d = '\\' then
                            skipEscapedCharacter ()
                        elif d = q then
                            stream.Skip()
                            closed <- true
                        elif d = '\r' || d = '\n' then
                            // Ordinary single- and double-quoted Groovy strings
                            // cannot cross an unescaped physical line ending.
                            // CRLF is observed at its CR, and bare CR must behave
                            // exactly like LF. Triple-quoted spans took the branch
                            // above and retain their multiline contract.
                            failed <- true
                        else
                            stream.Skip()

                    recordSignificant q (stream.Index - 1L) // a completed literal ends an expression
                elif c = '/' && stream.Peek(1) = '/' then
                    while
                        not stream.IsEndOfStream
                        && stream.Peek() <> '\r'
                        && stream.Peek() <> '\n'
                        do
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
                elif
                    c = '/'
                    && not (
                        endsExpression lastSig
                        || (((lastSig = '+' && priorSig = '+')
                             || (lastSig = '-' && priorSig = '-'))
                            && lastSigIndex = priorSigIndex + 1L
                            && endsExpression thirdSig)
                    )
                then
                    // FG-141: no left operand, so this `/` can only open a
                    // slashy. Skip its span; on no closer, rewind and read the
                    // `/` as the ordinary character it always was.
                    let before = stream.Index
                    stream.Skip()
                    let mutable closed = false

                    while not closed && not stream.IsEndOfStream do
                        let d = stream.Peek()

                        if d = '\\' && stream.Peek(1) = '/' then
                            stream.Skip(2)
                        elif d = '/' then
                            stream.Skip()
                            closed <- true
                        else
                            stream.Skip()

                    if closed then
                        let finish = stream.Index
                        let contentLength = int (finish - before - 2L)
                        stream.Seek(before + 1L)
                        let rawContent = stream.Read contentLength
                        stream.Seek finish
                        recordScalarContent rawContent stream
                        recordSignificant '\'' (finish - 1L) // a completed literal ends an expression
                    else
                        stream.Seek(before)
                        stream.Skip()
                        recordSignificant '/' before
                else
                    let significantIndex = stream.Index
                    if c = opening then depth <- depth + 1
                    elif c = closing then depth <- depth - 1
                    stream.Skip()

                    if c <> ' ' && c <> '\t' && c <> '\r' && c <> '\n' then
                        recordSignificant c significantIndex

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
