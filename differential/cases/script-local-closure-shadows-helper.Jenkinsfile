// FG-195 shape (a). A LOCAL closure shadows a preamble helper — Groovy resolves the
// local, and until the signature model landed Fogell refused the call by name.
// FG-189 folded in here: the shadowing local is only correct once it can be invoked.
def x() { return 'HELPER' }

pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def x = { 'LOCAL' }
                    echo "saw:${x()}"
                }
            }
        }
    }
}
