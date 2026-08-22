// FG-208. The pinned JUnit plugin represents a syntactically malformed .xml
// report as one synthetic failed test instead of failing the junit step.
pipeline {
    agent any
    stages {
        stage('malformed XML') {
            steps {
                script {
                    sh "rm -rf reports fg208-junit-malformed-xml.txt; mkdir -p reports; printf '%s' 'not-xml' > reports/malformed.xml"
                    def summary = junit(testResults: 'reports/malformed.xml')
                    def passes = summary.passCount

                    if (summary.totalCount == 1 && summary.failCount == 1 && summary.skipCount == 0 && passes == 0 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 1,1,0,0,Integer > fg208-junit-malformed-xml.txt'
                    } else {
                        sh 'printf wrong > fg208-junit-malformed-xml.txt'
                    }
                }
            }
        }
    }
}
