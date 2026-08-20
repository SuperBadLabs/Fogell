// FG-015 closure audit: an inclusive integer range drives a for-in loop.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def seen = ''
                    for (i in 1..3) {
                        seen = seen + i
                    }
                    sh "printf '%s' '${seen}' > range.txt"
                }
            }
        }
    }
}
