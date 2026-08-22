// FG-210. Literal allowEmptyResults true publishes a typed zero summary and
// continues when matched, well-formed reports contain no recognized result.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg210-junit-zero-result-allow-empty.txt; mkdir -p reports; printf '%s' '<testsuite name=\"zero-allowed\" tests=\"99\" failures=\"98\" errors=\"97\" skipped=\"96\"/>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml', allowEmptyResults: true)
                    def passes = summary.passCount
                    if (summary.totalCount == 0 && summary.failCount == 0 && summary.skipCount == 0 && passes == 0 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 0,0,0,0,Integer > fg210-junit-zero-result-allow-empty.txt'
                    } else {
                        sh 'printf wrong > fg210-junit-zero-result-allow-empty.txt'
                    }
                }
            }
        }
    }
}
