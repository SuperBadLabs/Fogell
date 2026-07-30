// FG-036. WITHOUT failFast, a failing branch does not stop its siblings: the
// slow branch still writes its file. An engine that aborts the parallel block
// on first failure would leave `slow.txt` missing, and the workspace hash says so.
pipeline {
    agent any
    stages {
        stage('Fan out') {
            parallel {
                stage('quick-fail') {
                    steps {
                        sh 'echo failing > failed.txt; exit 3'
                    }
                }
                stage('slow-ok') {
                    steps {
                        sh 'sleep 6; echo done > slow.txt'
                    }
                }
            }
        }
    }
}
