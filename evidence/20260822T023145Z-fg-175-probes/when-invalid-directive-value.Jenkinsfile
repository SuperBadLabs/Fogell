pipeline {
    agent any
    stages {
        stage('early') { steps { sh 'touch early.txt' } }
        stage('gated') {
            when {
                beforeAgent maybe
                branch 'main'
            }
            steps { sh 'touch gated.txt' }
        }
    }
}
