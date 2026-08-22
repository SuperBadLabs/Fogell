pipeline {
    agent any
    stages {
        stage('early') { steps { sh 'touch early.txt' } }
        stage('gated') {
            when { changeRequest(target: 'main', target: 'release') }
            steps { sh 'touch gated.txt' }
        }
    }
}
