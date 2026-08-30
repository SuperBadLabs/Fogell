// FG-044b(c). A credential argument key is a complete, case-sensitive identifier;
// a name that merely ends in `credentialsId` is not the supported key. Jenkins
// rejects these requests at the reached wrapper, and Fogell must fail closed before
// the wrapper body or its successor has an effect. The preceding marker and the
// separate positive control prevent a broad stage/preflight refusal from passing.
pipeline {
    agent any
    stages {
        stage('Credential key boundary') {
            steps {
                sh 'touch before-wrapper.txt'
                withCredentials([
                    string(credentialsId: 'fogell-token', variable: 'GOOD'),
                    string(notcredentialsId: 'fogell-token', variable: 'BAD_ID'),
                    string(credentialsId: 'fogell-token', notvariable: 'BAD_VARIABLE')
                ]) {
                    sh 'touch should-not-run.txt'
                }
                sh 'touch successor-should-not-run.txt'
            }
        }
    }
}
