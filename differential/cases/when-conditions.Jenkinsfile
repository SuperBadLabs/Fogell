pipeline {
    agent any
    environment {
        FOO = 'bar'
    }
    stages {
        stage('Skipped') {
            when {
                expression { return false }
            }
            steps {
                sh 'echo SHOULD-NOT-RUN > ran.txt'
            }
            post {
                always { sh 'echo POST-of-skipped-stage' }
            }
        }
        stage('Env match') {
            when {
                environment name: 'FOO', value: 'bar'
            }
            steps {
                sh 'echo env-matched'
            }
        }
        stage('Env mismatch') {
            when {
                environment name: 'FOO', value: 'nope'
            }
            steps {
                sh 'echo SHOULD-NOT-RUN >> ran.txt'
            }
        }
        stage('Not/allOf') {
            when {
                allOf {
                    environment name: 'FOO', value: 'bar'
                    not {
                        environment name: 'FOO', value: 'nope'
                    }
                }
            }
            steps {
                sh 'echo allof-matched'
            }
        }
    }
}
