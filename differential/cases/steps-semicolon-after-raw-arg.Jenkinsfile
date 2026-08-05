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
// hand-rolled character test had missed — `Lexeme` already enumerated all of
// them, and asking it is the fix.
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
