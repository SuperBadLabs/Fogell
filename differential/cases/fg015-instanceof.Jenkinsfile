// FG-015 closure audit: instanceof selects the String branch.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def value = 'text'
                    def answer = 'no'
                    if (value instanceof String) {
                        answer = 'yes'
                    }
                    sh "printf '%s' '${answer}' > instanceof.txt"
                }
            }
        }
    }
}
