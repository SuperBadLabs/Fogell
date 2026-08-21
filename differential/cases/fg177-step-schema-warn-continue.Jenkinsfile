// FG-177 slice 1. ShellStep's measured unknown-key behavior is warning plus
// execution, and the exact primary promotion must not discard the unknown key.
pipeline {
    agent any
    stages {
        stage('schema') {
            steps {
                script {
                    sh(script: 'printf warned > warned.txt', fogellProbeUnknown: true)
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
