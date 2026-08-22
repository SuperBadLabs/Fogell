// FG-210. A matched, well-formed report with no recognized result is terminal
// by default; hostile aggregate attributes cannot manufacture a result.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg210-junit-zero-result-default.txt; mkdir -p reports; printf '%s' '<testsuite name=\"zero-default\" tests=\"99\" failures=\"98\" errors=\"97\" skipped=\"96\"/>' > reports/result.xml"
                    junit(testResults: 'reports/result.xml')
                    sh 'printf wrong > fg210-junit-zero-result-default.txt'
                }
            }
        }
    }
}
