pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    try {
                        echo 'in'
                    } catch (MissingPropertyException e) {
                        echo 'caught'
                    }
                    echo 'after'
                }
            }
        }
    }
}
