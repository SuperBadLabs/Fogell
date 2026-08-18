// FG-189 (folded into FG-195), the explicit spelling. `f.call(x)` resolves exactly
// as `f(x)` does — same binding, same refusal contract. Measured 2026-08-13 as
// DENIED ('not a pure builtin') before the signature model; both spellings run now.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def f = { v -> "got:${v}" }
                    echo f('BARE')
                    echo f.call('EXPLICIT')
                }
            }
        }
    }
}
