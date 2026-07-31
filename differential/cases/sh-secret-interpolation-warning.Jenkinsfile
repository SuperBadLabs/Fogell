// FG-100. `sh` renders its argument through the Groovy string model, so a secret
// can now be interpolated INTO a shell command. Jenkins treats that as an
// insecurity and says so; this case measures whether it warns, and in what words.
pipeline {
    agent any
    stages {
        stage('Bind') {
            steps {
                withCredentials([string(credentialsId: 'fogell-token', variable: 'TOKEN')]) {
                    // The insecure form: Groovy interpolates the secret into the
                    // command line before /bin/sh ever sees it.
                    sh "echo interpolated:${TOKEN}"
                    // The safe form: single quotes, so the SHELL expands it.
                    sh 'echo expanded:$TOKEN'
                }
            }
        }
    }
}
