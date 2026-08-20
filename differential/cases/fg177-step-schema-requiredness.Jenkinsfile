// FG-177 slice 1. Primary-parameter promotion and minimum arity are independent:
// sh() throws, while echo() and body-only retry() continue.
pipeline {
    agent any
    stages {
        stage('schema') {
            steps {
                script {
                    try {
                        sh()
                        echo 'missing sh unexpectedly continued'
                    } catch (Exception ignored) {
                        sh 'printf caught > missing-sh-caught.txt'
                    }
                    echo()
                    retry() {
                        sh 'printf retried > retry-body-ran.txt'
                    }
                    sh 'printf continued > requiredness-continued.txt'
                }
            }
        }
    }
}
