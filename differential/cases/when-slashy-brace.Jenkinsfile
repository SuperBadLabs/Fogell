pipeline {
    agent any
    stages {
        stage("Gated") {
            when { expression { 'a}b' ==~ /a}b/ } }
            steps {
                sh "printf ran > gated.txt"
            }
        }
        stage("Skipped") {
            when { expression { 'a}b' ==~ /nope/ } }
            steps {
                sh "printf never > skipped.txt"
            }
        }
    }
}
