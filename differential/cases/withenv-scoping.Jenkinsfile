pipeline {
    agent any
    environment {
        OUTER = 'outer-value'
        SHADOWED = 'from-pipeline'
    }
    stages {
        stage('Work') {
            steps {
                withEnv(['ADDED=added-value', 'SHADOWED=from-withenv']) {
                    sh 'echo added=$ADDED shadowed=$SHADOWED outer=$OUTER'
                }
                sh 'echo after-added=[$ADDED] after-shadowed=$SHADOWED'
            }
        }
    }
}
