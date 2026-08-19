// FG-015 closure audit: a double-quoted GString may itself carry quoted shell text.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def who = 'world'
                    sh "printf '%s' \"${who}\" > nested-quote-gstring.txt"
                }
            }
        }
    }
}
