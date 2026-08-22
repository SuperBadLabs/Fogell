// FG-210. Literal allowEmptyResults true permits a no-match invocation,
// publishes a typed zero summary, emits both notices, and continues.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh 'rm -rf reports fg210-junit-no-match-allow-empty.txt; mkdir -p reports'
                    def summary = junit(testResults: 'reports/*.xml', allowEmptyResults: true)
                    def passes = summary.passCount
                    if (summary.totalCount == 0 && summary.failCount == 0 && summary.skipCount == 0 && passes == 0 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 0,0,0,0,Integer > fg210-junit-no-match-allow-empty.txt'
                    } else {
                        sh 'printf wrong > fg210-junit-no-match-allow-empty.txt'
                    }
                }
            }
        }
    }
}
