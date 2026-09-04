// FG-248. A backslash before a letter Groovy does not define — `\q` here, with
// `\s`, `\a`, `\e`, `\v`, `\x`, `\z` and the rest measured alike — is a compile
// refusal on Jenkins 2.568.1 (`unexpected char: '\'`) in every quoted form.
// This spelling sits inside `script { }`, so the SCRIPTED parser owns it;
// Fogell used to drop the backslash and run `printf "[q]"`. The receipt
// compares the typed refusal disposition, terminal result and real workspace
// hash; compiler wording is deliberately outside the compatibility claim.
pipeline {
    agent any
    stages {
        stage('must-not-run') {
            steps {
                script {
                    sh 'printf "[\q]\n"'
                }
            }
        }
    }
}
