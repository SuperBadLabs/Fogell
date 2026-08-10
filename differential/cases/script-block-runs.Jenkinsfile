// FG-160. A `script { }` body is scripted Groovy, handed to the Groovy interpreter and
// its steps performed LIVE as the interpreter reaches them (FG-172; this said "collected
// effects replayed in order", which described the batch model it replaced). Before this the body was parsed as
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
