// FG-208. Zero-byte detection precedes malformed-report extension gating, so
// empty .txt and uppercase .XML reports each become one synthetic failed test.
pipeline {
    agent any
    stages {
        stage('empty reports') {
            steps {
                script {
                    sh "rm -rf reports fg208-junit-empty-any-extension.txt; mkdir -p reports; : > reports/empty.txt; : > reports/empty.XML"
                    def summary = junit(testResults: 'reports/*')
                    def passes = summary.passCount

                    if (summary.totalCount == 2 && summary.failCount == 2 && summary.skipCount == 0 && passes == 0 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 2,2,0,0,Integer > fg208-junit-empty-any-extension.txt'
                    } else {
                        sh 'printf wrong > fg208-junit-empty-any-extension.txt'
                    }
                }
            }
        }
    }
}
