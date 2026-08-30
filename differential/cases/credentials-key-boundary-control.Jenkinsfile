// FG-044b(c) opposing control. A list with multiple exact, case-sensitive keys
// must still bind and run. Paired with credentials-key-boundary, this prevents a
// blanket refusal of multi-binding credential lists from looking fail-closed.
pipeline {
    agent any
    stages {
        stage('Credential key control') {
            steps {
                withCredentials([
                    string(credentialsId: 'fogell-token', variable: 'FIRST'),
                    string(credentialsId: 'fogell-token', variable: 'SECOND')
                ]) {
                    sh 'test "$FIRST" = "$SECOND" && printf exact > exact-keys.txt'
                }
            }
        }
    }
}
