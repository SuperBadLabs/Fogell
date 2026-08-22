pipeline {
    agent any
    stages {
        stage('early') { steps { sh 'touch early.txt' } }
        stage('gated') {
            when {
                allOf {
                    branch 'main'
                    environment name: 'T', value: 'a', value: 'b'
                }
            }
            steps { sh 'touch gated.txt' }
        }
    }
}
