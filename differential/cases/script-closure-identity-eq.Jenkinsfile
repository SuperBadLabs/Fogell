// FG-191. Closure identity, the plain spellings: two literals are not equal,
// an alias is. Groovy closures are reference-equal or not equal — never
// structurally compared.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def a = { 1 }
                    def b = { 1 }
                    def c = a
                    echo "distinct:${a == b}"
                    echo "alias:${a == c}"
                }
            }
        }
    }
}
