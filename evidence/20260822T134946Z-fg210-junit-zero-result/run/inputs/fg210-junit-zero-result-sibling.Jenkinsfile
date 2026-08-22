// FG-210. Zero-result reports do not poison a sibling containing one recognized
// passing testcase; only the real testcase contributes to the typed summary.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg210-junit-zero-result-sibling.txt; mkdir -p reports; printf '%s' '<testsuite name=\"zero\" tests=\"99\" failures=\"98\" errors=\"97\" skipped=\"96\"/>' > reports/a-zero.xml; printf '%s' '<testsuite name=\"valid\"><testcase name=\"pass\"/></testsuite>' > reports/b-valid.xml"
                    def summary = junit(testResults: 'reports/*.xml')
                    def passes = summary.passCount
                    if (summary.totalCount == 1 && summary.failCount == 0 && summary.skipCount == 0 && passes == 1 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 1,0,0,1,Integer > fg210-junit-zero-result-sibling.txt'
                    } else {
                        sh 'printf wrong > fg210-junit-zero-result-sibling.txt'
                    }
                }
            }
        }
    }
}
