pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<testsuite name=\"old\" time=\"2\"><testcase name=\"old-fail\"><failure/></testcase></testsuite>' > reports/old.xml; touch -d '2000-01-01 UTC' reports/old.xml"
                    def summary = junit(testResults: 'reports/*.xml', skipOldReports: true, allowEmptyResults: true)
                    echo "FG220=true-old-allowed;SUMMARY=${summary.totalCount},${summary.failCount},${summary.skipCount},${summary.passCount};DURATION=${summary.duration}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
