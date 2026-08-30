// FG-044b(b). Fogell exposes a file companion for a text credential as a
// convenience, but that companion must never overwrite an explicit binding from
// an enclosing withCredentials block. Jenkins has no companion; TOKEN_FILE below
// is therefore the outer credential value on both engines.
//
// The two bindings intentionally use the same credential. Equality proves the
// outer value survived without writing either secret or an engine-specific path
// into the workspace or console.
pipeline {
    agent any
    stages {
        stage('Nested credentials') {
            steps {
                withCredentials([string(credentialsId: 'fogell-token', variable: 'TOKEN_FILE')]) {
                    withCredentials([string(credentialsId: 'fogell-token', variable: 'TOKEN')]) {
                        sh 'test "$TOKEN_FILE" = "$TOKEN" && printf preserved > companion.txt'
                    }
                    sh 'test -n "$TOKEN_FILE" && printf restored > outer-after.txt'
                }
            }
        }
    }
}
