// FG-191. Two closures minted by two CALLS of one helper share an AST and
// nothing else — Groovy compares closures by IDENTITY, so they are not equal.
// MEASURED before the fix: the structural walk chased both captured cells into
// the cycle and the process died. Identity here is (AST node, captured env
// record) by reference, which this case and its two siblings pin from three
// directions.
def make() {
    def r
    r = { r }
    return r
}

pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def a = make()
                    def b = make()
                    echo "sameAst:${a == b}"
                    echo "after"
                }
            }
        }
    }
}
