// FG-122 round 2. Two of the three escape edges Codex raised on PR #36,
// measured rather than reasoned — each is a claim about Groovy's grammar, and
// this project does not ship a grammar claim it has not run.
//
// (a) OCTAL RANGE. Java permits a THREE-digit octal escape only when the first
//     digit is 0-3; `\400` is the two-digit `\40` (a space) followed by a
//     literal `0`. A greedy 1-3 digit reader yields U+0100 instead.
//
// (b) OCTAL DOLLAR. `\044` decodes to `$`. Interpolation is decided
//     LEXICALLY, before escapes are decoded, so the resulting dollar is
//     ordinary text — `\044MISSING` must stay `$MISSING`, not expand and not
//     fail the build on an undefined variable.
//
// (c) SIMPLE ESCAPES ON THE GSTRING PATH. `\b` and `\f` are decoded by
//     `simpleEscape`, but `escapedCharKeepingDollar` — the parser a
//     DOUBLE-QUOTED body goes through — carried its own copy of the
//     simple-letter map that had only \n, \t and \r. FG-122 shared the
//     NUMERIC map and left this one duplicated, so adding \b/\f made the two
//     copies DIVERGE, and the single-quoted receipt passed throughout. Both
//     parsers now share `simpleEscape` and differ only by `keepDollar`.
//     Raised by the pre-push verifier's model review on PR #36.
//
// (d) REPEATED `u`. Java's UnicodeEscape is `\ u+ HexDigit{4}`, so `\uu0041`
//     is `A` exactly as `\u0041` is. Accepting a single `u` let `uu0041` through
//     as text while the board claimed unicode escapes were handled.
//
// The slashy-string edge is NOT here. The first draft of this case carried
// `sh /printf '[\033]' > slashy.txt/` and Jenkins refused the whole script at
// COMPILE time — `expecting '}', found '[]'` — so that line proved nothing
// about slashy escapes and destroyed the two claims above with it. Sizing it
// needs a slashy form Jenkins accepts; FG-125 carries it.
//
// Each `printf` writes a file the workspace hash covers, so a wrong value
// cannot hide inside trace normalisation the way the original FG-122
// divergence nearly did.
pipeline {
    agent any
    stages {
        stage('one') {
            steps {
                sh 'printf "[\400]\n" > octal-range.txt; cat octal-range.txt'
                sh 'printf "[\101\102\103]\n" > octal-plain.txt; cat octal-plain.txt'
                sh "printf '[\044MISSING]\n' > octal-dollar.txt; cat octal-dollar.txt"
                sh "printf 'A\bB\fC' > simple-bf.txt; od -c simple-bf.txt"
                sh 'printf "[\u0041][\uu0041]\n" > unicode.txt; cat unicode.txt'
            }
        }
    }
}
