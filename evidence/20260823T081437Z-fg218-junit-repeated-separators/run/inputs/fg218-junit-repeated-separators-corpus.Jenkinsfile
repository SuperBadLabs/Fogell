// FG-218. The admitted corpus spells this Ant include with `//` after `**`.
// Ant discards the empty path token, so the report is selected exactly as if
// the separators were singular; case-sensitive matching and default excludes
// remain in force.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf module continued.txt; mkdir -p module/target/surefire-reports; printf '%s' '<testsuite name=\"suite\" time=\"1.25\"><testcase name=\"pass\"/></testsuite>' > module/target/surefire-reports/TEST-one.xml"
                    def summary = junit(testResults: '**//*target/surefire-reports/TEST-*.xml')
                    echo "FG218_SEPARATORS=corpus;SUMMARY=${summary.totalCount},${summary.failCount},${summary.skipCount},${summary.passCount};DURATION=${summary.duration}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
