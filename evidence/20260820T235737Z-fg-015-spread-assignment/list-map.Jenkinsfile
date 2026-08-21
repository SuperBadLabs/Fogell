pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                sh 'touch before-assignment.txt'
                script {
                    def rows = [[name: 'a'], [name: 'b']]
                    rows*.name = 'x'
                    sh "printf '%s' '${rows*.name}' > assignment-result.txt"
                }
                sh 'touch after-assignment.txt'
            }
        }
    }
}
