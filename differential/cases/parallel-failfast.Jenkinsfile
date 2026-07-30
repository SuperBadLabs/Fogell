// FG-036. WITH failFast, the first failure interrupts the running siblings, so
// the sleeping branch never reaches its second command. `late.txt` MUST be
// absent — that absence is the whole claim, and the workspace hash carries it.
//
// `failFast` sits at STAGE level, beside `parallel`. Jenkins REJECTS it inside
// the parallel block ("Expected a stage"); a receipt taught us that.
pipeline {
    agent any
    stages {
        stage('Fan out') {
            failFast true
            parallel {
                stage('quick-fail') {
                    steps {
                        sh 'sleep 1; echo failing > failed.txt; exit 3'
                    }
                }
                stage('interrupted') {
                    steps {
                        sh 'echo started > started.txt; sleep 45; echo late > late.txt'
                    }
                }
            }
        }
    }
}
