pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<testsuites time=\"999\"><testsuite name=\"cases\"><testcase classname=\"pkg.C\" name=\"one\" time=\"1.25\"/><testcase classname=\"pkg.C\" name=\"two\" time=\"2.5\"/></testsuite><testsuite name=\"override\" time=\"4.0\"><testcase classname=\"pkg.C\" name=\"ignored-child-time\" time=\"99\"/></testsuite></testsuites>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def property = summary.duration
                    def getter = summary.getDuration()
                    def parity = property == getter
                    def allFloat = property instanceof Float && getter instanceof Float && property instanceof Number && getter instanceof Number
                    def anyNonFloat = property instanceof Double || getter instanceof Double || property instanceof BigDecimal || getter instanceof BigDecimal
                    echo "FG214_DURATION=property:${property};getter:${getter};parity=${parity};allFloat=${allFloat};anyNonFloat=${anyNonFloat};counts=${summary.totalCount},${summary.failCount},${summary.skipCount},${summary.passCount}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
