// FG-044. `withCredentials([string(...)])` — 19 of the 23 corpus files that use
// credentials take this shape.
//
// MEASURED on the pinned Jenkins: the VALUE lands in the named variable
// (`env | grep -c '^TOKEN='` is 1, `${#TOKEN}` is the secret's length), the console
// shows `****` instead of the value, and the variable is UNSET after the block.
//
// That measurement is why FG-070's original "secrets never in the environment" design
// had to be re-scoped: every credential user reads $TOKEN, so binding only a path
// would break the lift-and-shift promise. Masking on every output path is what
// protects the value (FG-071), not its absence from the environment.
//
// The workspace deliberately records the LENGTH and a masked echo rather than the
// secret itself, so the receipt proves the binding without committing a value.
pipeline {
    agent any
    stages {
        stage('Bind') {
            steps {
                withCredentials([string(credentialsId: 'fogell-token', variable: 'TOKEN')]) {
                    sh 'echo "len=${#TOKEN}" > len.txt'
                    sh 'env | grep -c "^TOKEN=" > in_env.txt'
                }
                sh 'echo "after=[${TOKEN:-unset}]" > after.txt'
            }
        }
    }
}
