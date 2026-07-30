// FG-049 review fix. A failing `post` on an OTHERWISE SUCCESSFUL stage must fail
// the build AND stop the pipeline. The post context's failure flag was created
// fresh and discarded, so the build went red while later stages kept running.
// Caught by a Codex review comment on PR #13: `later.txt` must be absent.
pipeline {
    agent any
    stages {
        stage('Succeeds but post fails') {
            steps {
                sh 'echo body > body.txt'
            }
            post {
                always {
                    sh 'echo post-ran > post.txt; exit 4'
                }
            }
        }
        stage('Must not run') {
            steps {
                sh 'echo SHOULD-NOT-RUN > later.txt'
            }
        }
    }
}
