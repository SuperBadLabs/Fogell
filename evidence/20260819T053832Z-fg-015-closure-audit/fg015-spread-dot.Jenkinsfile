// FG-015 closure audit: spread-dot projects one property from every list element.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def rows = [[name: 'a'], [name: 'b']]
                    def names = rows*.name
                    sh "printf '%s' '${names}' > spread-dot.txt"
                }
            }
        }
    }
}
