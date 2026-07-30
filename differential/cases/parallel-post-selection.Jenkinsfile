// FG-036/049 review fix. A stage owning a `parallel` block whose branch FAILS
// must select `post { failure }`, not `post { success }`. The branch context sent
// its status straight to the global sink, bypassing the enclosing stage's, so the
// stage's own result stayed Success — meaning a success-only publish or deploy
// step would fire on a red build. Caught by a Codex review comment on PR #13.
pipeline {
    agent any
    stages {
        stage('Fan out') {
            parallel {
                stage('fails') {
                    steps {
                        sh 'exit 6'
                    }
                }
                stage('ok') {
                    steps {
                        sh 'echo fine > fine.txt'
                    }
                }
            }
            post {
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
