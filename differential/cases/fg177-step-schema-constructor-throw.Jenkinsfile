// FG-177 slice 1. EchoStep binds a named map containing an unknown key to its
// String constructor parameter and throws. The throw is script-catchable; the
// malformed step and the statement after it must perform no work.
pipeline {
    agent any
    stages {
        stage('schema') {
            steps {
                script {
                    try {
                        echo(message: 'must-not-print', fogellProbeUnknown: true)
                        sh 'printf wrong > wrong.txt'
                    } catch (Exception ignored) {
                        sh 'printf caught > caught.txt'
                    }
                }
            }
        }
    }
}
