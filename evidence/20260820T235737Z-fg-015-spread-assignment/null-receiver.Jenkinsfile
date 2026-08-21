pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                sh 'touch before-assignment.txt'
                script {
                    def rows = null
                    rows*.name = 'x'
                    sh "printf '%s' '${rows}' > assignment-result.txt"
                }
                sh 'touch after-assignment.txt'
            }
        }
    }
}
