// FG-034/035 review fix. Control-flow steps must COMPOSE. A `timeout` inside a
// `retry` inside a `dir` used to reach the plain step executor, which does not
// know those names, so the build failed as "unsupported step". Caught by a
// Codex review comment on PR #12. The attempt count in the workspace is the
// evidence that the retry really ran the nested body twice.
pipeline {
    agent any
    stages {
        stage('Composed') {
            steps {
                dir('work') {
                    retry(2) {
                        timeout(time: 20, unit: 'SECONDS') {
                            sh 'echo attempt >> attempts.txt; exit 1'
                        }
                    }
                }
            }
        }
    }
}
