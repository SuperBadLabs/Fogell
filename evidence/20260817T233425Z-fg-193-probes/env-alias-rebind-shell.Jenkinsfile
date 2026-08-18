pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def saved = env
                    def env = saved
                    env.FOO = 'bar'
                    sh 'echo shell:$FOO'
                }
            }
        }
    }
}
