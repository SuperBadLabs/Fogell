// FG-216. A case-only path mismatch is the existing no-report terminal path;
// it must not parse the differently cased report or reach the successor.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<testsuite name=\"upper\" time=\"1.25\"><testcase name=\"pass\"/></testsuite>' > reports/Result.XML"
                    junit(testResults: 'reports/result.xml')
                    sh 'printf wrong > continued.txt'
                }
            }
        }
    }
}
