pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    dir('s') {
                        withEnv(['A=one']) { sh 'true' }
                        echo "after:${env.A}"
                    }
                }
            }
        }
    }
}
