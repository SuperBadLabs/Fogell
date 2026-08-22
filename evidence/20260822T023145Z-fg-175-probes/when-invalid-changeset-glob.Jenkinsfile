pipeline {
    agent any
    stages {
        stage('early') { steps { sh 'touch early.txt' } }
        stage('gated') {
            when { changeset glob: '**/*.java' }
            steps { sh 'touch gated.txt' }
        }
    }
}
