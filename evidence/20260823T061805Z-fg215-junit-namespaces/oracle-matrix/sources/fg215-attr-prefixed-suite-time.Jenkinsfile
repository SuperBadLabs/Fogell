pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<j:testsuite xmlns:j=\"urn:fg215:element\" xmlns:a=\"urn:fg215:attr\" name=\"owner\" a:time=\"7.5\"><j:testcase classname=\"pkg.C\" name=\"case\" time=\"1.25\"/></j:testsuite>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def property = summary.duration
                    def getter = summary.getDuration()
                    echo "FG215_ATTR=prefixed-suite-time;SUMMARY=${summary.totalCount},${summary.failCount},${summary.skipCount},${summary.passCount};PROPERTY=${property};GETTER=${getter};FLOAT=${property instanceof Float && getter instanceof Float};STATUS=${currentBuild.currentResult}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
