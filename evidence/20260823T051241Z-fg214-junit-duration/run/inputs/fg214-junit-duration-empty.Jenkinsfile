pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh 'rm -rf reports continued.txt; mkdir -p reports'
                    def summary = junit(testResults: 'reports/*.xml', allowEmptyResults: true)
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
