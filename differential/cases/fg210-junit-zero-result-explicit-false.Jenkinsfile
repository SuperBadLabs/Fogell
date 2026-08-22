// FG-210. Literal allowEmptyResults false preserves the default terminal path
// for a matched, well-formed report with no recognized result.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg210-junit-zero-result-explicit-false.txt; mkdir -p reports; printf '%s' '<testsuite name=\"zero-explicit-false\"/>' > reports/result.xml"
                    junit(testResults: 'reports/result.xml', allowEmptyResults: false)
                    sh 'printf wrong > fg210-junit-zero-result-explicit-false.txt'
                }
            }
        }
    }
}
