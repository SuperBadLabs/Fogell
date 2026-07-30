// FG-044. `file(...)` credentials — 3 corpus files. Jenkins binds the requested
// variable to a PATH to a temporary file holding the content, NOT to the content.
// Both reviewers caught that the first implementation bound `<VAR>_CONTENT` and never
// `<VAR>` at all, while the comment claimed otherwise — so every file() body ran with
// its variable unset.
//
// The receipt compares what the script can OBSERVE: that the variable names a readable
// file, and the content it holds. The path itself differs between engines by
// construction and is not compared.
pipeline {
    agent any
    stages {
        stage('Bind') {
            steps {
                withCredentials([file(credentialsId: 'fogell-file', variable: 'CERT')]) {
                    sh 'test -r "$CERT" && echo readable > readable.txt'
                    sh 'cat "$CERT" > content.txt'
                }
            }
        }
    }
}
