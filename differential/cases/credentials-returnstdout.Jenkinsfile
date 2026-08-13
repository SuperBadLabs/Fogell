// FG-174. A captured value must be the SECRET, not the mask.
//
// `Stdout` on a `StepResult` is masked, which is right for everything that prints or is
// compared — and wrong for the one consumer that is not printing: the value handed back
// to the pipeline. Jenkins masks the LOG, not the variable.
//
// MEASURED before the fix: this printed `captured-length=12` on Jenkins and
// `captured-length=4` on Fogell, because `****` is what Fogell captured. A pipeline that
// captures a token and passes it to the next command therefore authenticated with four
// asterisks — it would fail, or worse, take a different branch. Raised in review on
// PR #53.
//
// THE LENGTH IS THE ASSERTION, and the secret itself is never printed or written: a case
// that echoed the token to prove it was unmasked would put a credential in a receipt that
// gets committed. The length separates the two possible answers (12 vs 4) without
// carrying either.
//
// Masking is NOT relaxed by this: `credentials-string`, `credentials-userpass`,
// `credentials-userpass-masking`, `echo-credential-masking` and `credentials-file` all
// still hold the console guarantee, and this case would diverge on output if the token
// reached the log.
pipeline {
    agent any
    stages {
        stage('Capture') {
            steps {
                withCredentials([string(credentialsId: 'fogell-token', variable: 'TOKEN')]) {
                    script {
                        def t = sh(script: 'printf %s "$TOKEN"', returnStdout: true)
                        sh "echo captured-length=${t.length()} > len.txt"
                    }
                }
            }
        }
    }
}
