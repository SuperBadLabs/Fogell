pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    try {
                        def s = null
                        s.FOO = 'x'
                        echo 'no-throw'
                    } catch (Exception e) {
                        echo 'caught-other'
                    }
                    echo 'after'
                }
            }
        }
    }
}
