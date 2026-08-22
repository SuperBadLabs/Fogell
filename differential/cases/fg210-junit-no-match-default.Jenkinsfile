// FG-210. No report match is terminal by default and does not run a successor.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh 'rm -rf reports fg210-junit-no-match-default.txt; mkdir -p reports'
                    junit(testResults: 'reports/*.xml')
                    sh 'printf wrong > fg210-junit-no-match-default.txt'
                }
            }
        }
    }
}
