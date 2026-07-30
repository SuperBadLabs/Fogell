// FG-044. Does Jenkins mask a usernamePassword's USERNAME? MEASURED: YES — both the
// username and the password come back `****`.
//
// This case exists because a review asked for the username to be exported unmasked,
// citing a comment I had written asserting Jenkins does not mask it. I had never checked.
// The receipt settled it against both of us, and the earlier credentials-userpass case
// could not have: it wrote the username to a FILE, and file contents are not masked.
// Printing to STDOUT is the only place the difference is observable.
pipeline {
    agent any
    stages {
        stage('Bind') {
            steps {
                withCredentials([usernamePassword(credentialsId: 'fogell-userpass', usernameVariable: 'DEPLOY_USER', passwordVariable: 'DEPLOY_PASS')]) {
                    sh 'echo "user-on-stdout=$DEPLOY_USER"'
                    sh 'echo "pass-on-stdout=$DEPLOY_PASS"'
                }
            }
        }
    }
}
