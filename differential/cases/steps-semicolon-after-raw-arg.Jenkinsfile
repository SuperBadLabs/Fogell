// FG-138. A `;` terminates an UNQUOTED argument value, so the step after it runs.
//
// `returnStatus: true` is genuinely raw — no quotes — which is the whole point.
// The quoted shape (`label: 'x'; sh …`) never needed this: the string-literal
// parser consumes the value and the `;` reaches the step-block separator loop.
// A case built on quoted values would pass with the bug present, and an earlier
// version of exactly that was caught doing so on PR #40.
//
// The fix is `Lexeme.stringSpanRaw`: literal SPANS are consumed whole, so `;`
// can be a stop character without truncating an expression that carries one
// inside a literal. FG-134 spent five review rounds discovering string forms a
// hand-rolled character test had missed.
//
// WHICH FORMS, exactly: single, double and triple quoted spans. SLASHY and
// DOLLAR-SLASHY are NOT protected here — a `;` inside `/…/` still terminates the
// raw argument. This comment previously said `Lexeme` enumerated ALL string forms
// and that asking it was the fix; that was the same "every form" overclaim the
// lexer comment already had to retract. Since FG-147 an unparseable parenthesised
// body is refused rather than guessed at, so the slashy gap is a REFUSAL, not a
// silent wrong value — but it is a gap, and FG-144 tracks it.
pipeline {
    agent any
    stages {
        stage('one') {
            steps {
                sh script: 'echo a > a.txt', returnStatus: true; sh 'echo b > b.txt'
            }
        }
    }
}
