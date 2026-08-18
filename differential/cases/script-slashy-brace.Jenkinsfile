pipeline {
    agent any
    stages {
        stage("Slashy") {
            steps {
                script {
                    def pattern = /}/
                    sh "printf saw:${pattern} > slashy.txt"
                    sh "cat slashy.txt"
                }
            }
        }
    }
}
