pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    try {
                        echo 'in'
                    } catch (groovy.lang.MissingPropertyException e) {
                        echo 'caught'
                    }
                    echo 'after'
                }
            }
        }
    }
}
