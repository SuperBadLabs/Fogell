// FG-212. A reached owner's raw name makes its direct missing-name testcase
// admissible for both an arbitrary document root and a direct testsuite edge.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg212-junit-missing-name-owner-fallback.txt; mkdir -p reports; printf '%s' '<wrapper name=\"RootFallback\"><testcase/><testsuite name=\"SuiteFallback\"><testcase/></testsuite></wrapper>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def passes = summary.passCount
                    if (summary.totalCount == 2 && summary.failCount == 0 && summary.skipCount == 0 && passes == 2 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 2,0,0,2,Integer > fg212-junit-missing-name-owner-fallback.txt'
                    } else {
                        sh 'printf wrong > fg212-junit-missing-name-owner-fallback.txt'
                    }
                }
            }
        }
    }
}
