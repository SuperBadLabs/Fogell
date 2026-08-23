// FG-216. JUnit leaves Ant report-pattern matching case-sensitive. The
// differently cased failing sibling must remain outside the selected set.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<testsuite name=\"lower\" time=\"1.25\"><testcase name=\"pass\"/></testsuite>' > reports/result-pass.xml; printf '%s' '<testsuite name=\"upper\" time=\"9\"><testcase name=\"fail\"><failure/></testcase></testsuite>' > reports/RESULT-fail.xml"
                    def summary = junit(testResults: 'reports/result-*.xml')
                    echo "FG216_GLOB_CASE=selection;SUMMARY=${summary.totalCount},${summary.failCount},${summary.skipCount},${summary.passCount};DURATION=${summary.duration}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
