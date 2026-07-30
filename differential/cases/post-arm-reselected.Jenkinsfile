// FG-049 review fix. Post arms must be re-selected against the CURRENT result. On
// a SUCCESSFUL stage whose `always` block fails, `failure` becomes eligible and
// `success` must NOT run — otherwise success-only publication fires after a post
// failure. Caught by a Codex review comment on PR #13. `wrong-arm.txt` must be
// absent and `right-arm.txt` present.
pipeline {
    agent any
    stages {
        stage('Succeeds, post fails') {
            steps {
                sh 'echo body > body.txt'
            }
            post {
                always {
                    sh 'exit 9'
                }
                success {
                    sh 'echo MUST-NOT-RUN > wrong-arm.txt'
                }
                failure {
                    sh 'echo correct > right-arm.txt'
                }
            }
        }
    }
}
