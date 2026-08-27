pipeline {
    agent any
    stages {
        stage("Grouped") {
            steps {
                script {
                    def xs = ['first', 'second']
                    def v = (xs
[0])
                    sh "printf got:${v} > out.txt"
                }
            }
        }
    }
}
