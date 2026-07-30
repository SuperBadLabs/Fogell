// FG-046 review fix, PR #17 round 6. A GString may hold a real Groovy EXPRESSION, not just
// a variable path — Jenkins displays "Approve build 2?". The identifier-only substitution
// left it verbatim. The bounded interpreter already in this project evaluates it, with an
// EMPTY step vocabulary so a prompt cannot invoke build steps.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                timeout(time: 4, unit: 'SECONDS') {
                    input message: "Approve build ${1 + 1}?"
                }
            }
        }
    }
}
