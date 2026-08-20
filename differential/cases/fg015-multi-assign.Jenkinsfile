// FG-015 closure audit: a two-name declaration binds the matching list elements.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def (left, right) = ['L', 'R']
                    sh "printf '%s' '${left}:${right}' > multi-assign.txt"
                }
            }
        }
    }
}
