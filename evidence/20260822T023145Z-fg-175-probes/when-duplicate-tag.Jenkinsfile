pipeline {
    agent any
    stages {
        stage('early') { steps { sh 'touch early.txt' } }
        stage('gated') {
            when { tag pattern: 'v1', pattern: 'v2' }
            steps { sh 'touch gated.txt' }
        }
    }
}
