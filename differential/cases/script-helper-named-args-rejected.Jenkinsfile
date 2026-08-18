// FG-195 shape (d). A named-argument group is ONE Map argument, so a no-arg helper
// called with `foo: 1` is a one-argument call with no matching overload — rejected
// by both engines. Found inside the guard written for shape (c): counting
// positionals alone admitted it and ran the body under a green build.
def zero() { return 'z' }

pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    echo "saw:${zero(foo: 1)}"
                }
            }
        }
    }
}
