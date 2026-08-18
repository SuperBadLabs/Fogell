// FG-191. Two closures minted by ONE literal in a loop are DISTINCT — Groovy
// closures are objects per evaluation, not per source location. The identity
// model's first spelling compared (AST, captured-env-record) and a while body
// that assigns through cells never changes the record, so this printed
// loopEq:true — a false equality caught by this branch's own probe before it
// shipped. Every evaluation now mints a fresh record; the cells inside are
// shared, so capture stays by reference.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def xs = []
                    def i = 0
                    while (i < 2) {
                        xs = xs + [{ 9 }]
                        i = i + 1
                    }
                    echo "loopEq:${xs[0] == xs[1]}"
                }
            }
        }
    }
}
