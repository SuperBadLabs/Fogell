// FG-160. An unbound name inside a `script { }` body must FAIL, as Jenkins does with
// MissingPropertyException — not read as null. The interpreter's lax mode wrote
// `bare:null` and exited 0 here, which is a success where Jenkins fails.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    sh "echo bare:${MISSING_BARE_VAR}"
                }
            }
        }
    }
}
