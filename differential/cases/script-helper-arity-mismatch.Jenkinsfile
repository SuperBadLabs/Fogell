// FG-195 shape (c). A one-arg helper called with NONE has no matching overload —
// Groovy rejects the call (MissingMethodException at runtime) and so does Fogell,
// naming the attempted signature. Before the signature model the body simply ran
// with nothing bound, and the build reported success.
def constant(v) { return v }

pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    echo "saw:${constant()}"
                }
            }
        }
    }
}
