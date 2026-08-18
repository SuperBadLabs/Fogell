pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    try {
                        echo 'in'
                    } catch (MissingPropertyException e) {
                        echo 'mpe'
                    } catch (Exception e) {
                        echo 'other'
                    }
                    echo 'after'
                }
            }
        }
    }
}
