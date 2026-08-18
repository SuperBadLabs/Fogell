pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def local = [:]
                    def other = local
                    other.FOO = 'x'
                    echo "alias:${local.FOO}"
                }
            }
        }
    }
}
