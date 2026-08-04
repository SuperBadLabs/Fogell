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
let private numericEscape: P<char> =
    (skipChar 'u'
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

let private simpleEscape (c: char) =
    match c with
    | 'n' -> '\n'
    | 't' -> '\t'
    | 'r' -> '\r'
    | 'b' -> '\b'
    | 'f' -> '\012'
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

// --- balanced raw capture --------------------------------------------------

/// Capture the raw source of a balanced region, skipping over strings and
/// comments so their delimiters never affect the depth count. Used for
/// argument lists and for `expression { }` bodies that the interpreter owns.
let balancedRaw (opening: char) (closing: char) : P<string> =
    let inner (stream: CharStream<unit>) =
        if stream.Peek() <> opening then
            Reply(Error, expected $"'{opening}'")
        else
            let start = stream.Index
            stream.Skip()
            let mutable depth = 1
            let mutable failed = false

            while depth > 0 && not failed && not (stream.IsEndOfStream) do
                let c = stream.Peek()

                if c = '\'' || c = '"' then
                    // skip a string literal wholesale
                    let q = c
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
                else
                    if c = opening then depth <- depth + 1
                    elif c = closing then depth <- depth - 1
                    stream.Skip()

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
