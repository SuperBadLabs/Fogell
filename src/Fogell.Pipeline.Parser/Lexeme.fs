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

let private escapedChar: P<char> =
    skipChar '\\' >>. anyChar
    |>> function
        | 'n' -> '\n'
        | 't' -> '\t'
        | 'r' -> '\r'
        | c -> c

let private quoted (q: string) : P<string> =
    between (skipString q) (skipString q) (
        manyChars (escapedChar <|> satisfy (fun c -> c <> q.[0] && c <> '\n')))

let private tripleQuoted (q: string) : P<string> =
    between (skipString q) (skipString q) (
        manyCharsTill (escapedChar <|> anyChar) (lookAhead (skipString q)))

/// As [escapedChar], but an escaped $ keeps its backslash.
///
/// REVIEW FIX (Codex, PR #14 round 7): in a GString, "\$BUILD_NUMBER" is the
/// literal text $BUILD_NUMBER — Groovy does not interpolate it. The backslash
/// was stripped here, BEFORE the value was classified as interpolating, so the
/// interpolation pass expanded it and the step ran with a different environment than
/// Jenkins. The marker now survives to the interpolation pass, which honours it and
/// then removes it.
let private escapedCharKeepingDollar: P<string> =
    skipChar '\\' >>. anyChar
    |>> function
        | 'n' -> "\n"
        | 't' -> "\t"
        | 'r' -> "\r"
        // A NUL sentinel, not "\$": REVIEW FIX (Codex, PR #14 round 9). `"\\$X"` is
        // an escaped BACKSLASH followed by a live interpolation — Groovy yields one
        // backslash and expands `$X`. Decoding the escaped dollar to "\$" made the two
        // cases indistinguishable downstream, so that value came out as a literal
        // `$X`. NUL cannot occur in an environment value, so it cannot collide.
        | '$' -> "\u0000"
        | c -> string c

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
              attempt (quoted "/" |>> fun s -> s, true) ])

let stringLiteral: P<string> =
    lexeme (
        choice
            [ attempt (tripleQuoted "'''")
              attempt (tripleQuoted "\"\"\"")
              attempt (quoted "'")
              attempt (quoted "\"")
              attempt (quoted "/") ])

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
