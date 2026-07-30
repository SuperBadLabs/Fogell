pipeline {
    agent any
    stages {
        stage('Work') {
            steps {
                sh 'exit 5'
            }
            post {
                always { sh "echo POST-always" }
                changed { sh "echo POST-changed" }
                fixed { sh "echo POST-fixed" }
                regression { sh "echo POST-regression" }
                aborted { sh "echo POST-aborted" }
                failure { sh "echo POST-failure" }
                success { sh "echo POST-success" }
                unstable { sh "echo POST-unstable" }
                cleanup { sh "echo POST-cleanup" }
            }
        }
    }
}
