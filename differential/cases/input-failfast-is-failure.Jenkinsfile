// FG-046 / FG-002e. When a failFast SIBLING fails while another branch waits at an
// `input`, the build is FAILURE — the sibling's failure is the cause and the interruption
// is collateral. Reporting `aborted` here is the collateral-outranks-cause bug, which this
// project has now produced four times (shell steps, stash, unstash, deleteDir) before
// `input` made it five. This case exists so the fifth is the last.
pipeline {
    agent any
    stages {
        stage('Fan out') {
            failFast true
            parallel {
                stage('fails fast') {
                    steps {
                        sh 'sleep 1; exit 3'
                    }
                }
                stage('waiting at a gate') {
                    steps {
                        timeout(time: 60, unit: 'SECONDS') {
                            input 'Proceed?'
                        }
                    }
                }
            }
        }
    }
}
