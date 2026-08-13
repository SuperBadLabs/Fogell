pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    ['alpha', 'beta'].each { name ->
                        sh "echo item:${name}"
                    }
                }
            }
        }
    }
}
