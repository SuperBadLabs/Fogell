// FG-160. Only ONE branch may run. Parsing a script body as Declarative steps read
// `if (…)` as a step named `if` and `else` as another, so the structure was accepted and
// the meaning lost; this case fails loudly if that ever returns.
pipeline {
    agent any
    environment { TARGET = 'prod' }
    stages {
        stage('Gate') {
            steps {
                script {
                    if (env.TARGET == 'prod') {
                        sh 'echo TOOK-TRUE'
                    } else {
                        sh 'echo TOOK-FALSE'
                    }
                }
            }
        }
    }
}
