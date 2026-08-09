// FG-160. A `script { }` body is scripted Groovy, handed to the Groovy interpreter and
// its collected step effects replayed in order. Before this the body was parsed as
// Declarative steps and the build FAILED with no output.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    sh 'echo INSIDE-SCRIPT'
                    sh 'echo SECOND-STEP'
                }
            }
        }
    }
}
