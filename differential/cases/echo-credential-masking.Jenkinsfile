// FG-044b. `echo` renders text itself, and masking lived ONLY on the shell path — so
// `echo "${TOKEN}"` published the credential verbatim where Jenkins prints `****`.
//
// MEASURED, and richer than the ticket said: Jenkins WARNS (three lines) that a secret
// reached a step argument through GString interpolation, THEN prints the masked value.
// It also interpolates a GString and leaves a single-quoted argument literal. All four
// behaviours are in this one case.
pipeline {
    agent any
    environment {
        GREETING = 'hello'
    }
    stages {
        stage('Echo') {
            steps {
                withCredentials([string(credentialsId: 'fogell-token', variable: 'TOKEN')]) {
                    echo "token is ${TOKEN}"
                    echo "greeting is ${GREETING}"
                }
                echo 'literal ${GREETING}'
            }
        }
    }
}
