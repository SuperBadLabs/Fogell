pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<j:testsuites xmlns:j=\"urn:fg215:element\" xmlns:a=\"urn:fg215:attr\"><j:testsuite a:name=\"pref-suite-a\" name=\"plain-suite-a\" a:time=\"40\" time=\"4\"><j:testcase a:classname=\"pkg.PrefA\" classname=\"pkg.PlainA\" a:name=\"pref-case-a\" name=\"plain-case-a\" a:time=\"9\" time=\"1\"/></j:testsuite><j:testsuite a:name=\"pref-suite-b\" name=\"plain-suite-b\"><j:testcase a:classname=\"pkg.PrefB\" classname=\"pkg.PlainB\" a:name=\"pref-case-b\" name=\"plain-case-b\" a:time=\"9\" time=\"1\"/></j:testsuite></j:testsuites>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def property = summary.duration
                    def getter = summary.getDuration()
                    echo "FG215_ATTR=duplicate-prefixed-first;SUMMARY=${summary.totalCount},${summary.failCount},${summary.skipCount},${summary.passCount};PROPERTY=${property};GETTER=${getter};FLOAT=${property instanceof Float && getter instanceof Float};STATUS=${currentBuild.currentResult}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
