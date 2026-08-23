// FG-219. Repeated trailing separators compose with Ant's empty-token rule and
// retain the same recursive directory shorthand.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf outer outside continued.txt; mkdir -p outer/reports/deep outside; printf '%s' '<testsuite name=\"top\" time=\"1.25\"><testcase name=\"pass\"/></testsuite>' > outer/reports/top.xml; printf '%s' '<testsuite name=\"deep\" time=\"2.5\"><testcase name=\"fail\"><failure/></testcase></testsuite>' > outer/reports/deep/result.xml; printf '%s' '<testsuite name=\"outside\" time=\"9\"><testcase name=\"outside-fail\"><failure/></testcase></testsuite>' > outside/result.xml"
                    def summary = junit(testResults: 'outer/reports//')
                    echo "FG219_TRAILING=doubled;SUMMARY=${summary.totalCount},${summary.failCount},${summary.skipCount},${summary.passCount};DURATION=${summary.duration}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
