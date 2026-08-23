pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<j:testsuites xmlns:j=\"urn:fg215:element\" xmlns:a=\"urn:fg215:attr\"><j:testsuite name=\"plain-suite-a\" a:name=\"pref-suite-a\" time=\"4\" a:time=\"40\"><j:testcase classname=\"pkg.PlainA\" a:classname=\"pkg.PrefA\" name=\"plain-case-a\" a:name=\"pref-case-a\" time=\"1\" a:time=\"9\"/></j:testsuite><j:testsuite name=\"plain-suite-b\" a:name=\"pref-suite-b\"><j:testcase classname=\"pkg.PlainB\" a:classname=\"pkg.PrefB\" name=\"plain-case-b\" a:name=\"pref-case-b\" time=\"1\" a:time=\"9\"/></j:testsuite></j:testsuites>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def property = summary.duration
                    def getter = summary.getDuration()
                    echo "FG215_ATTR=duplicate-plain-first;SUMMARY=${summary.totalCount},${summary.failCount},${summary.skipCount},${summary.passCount};PROPERTY=${property};GETTER=${getter};FLOAT=${property instanceof Float && getter instanceof Float};STATUS=${currentBuild.currentResult}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
