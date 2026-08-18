// FG-195 shape (b). Two preamble helpers sharing a name are an ordinary overload
// pair, resolved by ARITY per call. The map-by-name model ran the one-arg body for
// a zero-arg call (last declaration won); the any-duplicate refusal that replaced
// it refused work Jenkins runs. Both calls below must pick their own body.
def pick() { return 'zero' }
def pick(v) { return 'one' }

pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    echo "none:${pick()}"
                    echo "one:${pick('x')}"
                }
            }
        }
    }
}
