pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                sh 'touch before-assignment.txt'
                script {
                    def groups = [[child: [name: 'a']], [child: null], [child: [name: 'b']]]
                    groups*.child?.name = 'x'
                    sh "printf '%s' '${groups*.child*.name}' > assignment-result.txt"
                }
                sh 'touch after-assignment.txt'
            }
        }
    }
}
