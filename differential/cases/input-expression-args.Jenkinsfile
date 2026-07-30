// FG-046 review fix, PR #17 round 7. An UNQUOTED argument is a Groovy EXPRESSION, not
// text: Jenkins evaluates `input message: env.TARGET` and shows the value, where Fogell
// displayed the source text `env.TARGET`. Three kinds, not two — single-quoted is literal,
// double-quoted is a GString, unquoted is an expression.
//
// The prompt also carries a `}` inside a quoted string, which the previous flat `[^}]*`
// matcher truncated. Groovy's boundary is the BALANCED brace.
pipeline {
    agent any
    environment {
        TARGET = 'production'
    }
    stages {
        stage('Gate') {
            steps {
                timeout(time: 4, unit: 'SECONDS') {
                    input message: env.TARGET, ok: "Close ${'}'} now"
                }
            }
        }
    }
}
