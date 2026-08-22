// FG-208. One malformed .xml contributes one synthetic failure without
// discarding the ordinary cases parsed from another matched report.
pipeline {
    agent any
    stages {
        stage('mixed reports') {
            steps {
                script {
                    sh "rm -rf reports fg208-junit-malformed-mixed.txt; mkdir -p reports; printf '%s' '<testsuite name=\"valid\" tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"ok\"/></testsuite>' > reports/valid.xml; printf '%s' 'not-xml' > reports/malformed.xml"
                    def summary = junit(testResults: 'reports/*.xml')

                    if (summary.totalCount == 2 && summary.failCount == 1 && summary.skipCount == 0 && summary.passCount == 1) {
                        sh 'printf 2,1,0,1 > fg208-junit-malformed-mixed.txt'
                    } else {
                        sh 'printf wrong > fg208-junit-malformed-mixed.txt'
                    }
                }
            }
        }
    }
}
