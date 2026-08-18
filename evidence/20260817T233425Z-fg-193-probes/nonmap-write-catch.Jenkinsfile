pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    try {
                        def s = 'str'
                        s.FOO = 'x'
                        echo 'no-throw'
                    } catch (Exception e) {
                        echo 'caught'
                    }
                    echo 'after'
                }
            }
        }
    }
}
