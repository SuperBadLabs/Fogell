// FG-191. The reviewer's original construction: two self-referential closures
// compared with ==. Distinct literals differ in their ASTs, so identity answers
// false before any walk — this spelling never recursed, and the one that did
// (same AST via two calls) is the sibling case.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def c
                    c = { c }
                    def d
                    d = { d }
                    echo "eq:${c == d}"
                }
            }
        }
    }
}
