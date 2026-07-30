// FG-036. `parallelsAlwaysFailFast()` is the pipeline-wide form of the same
// directive, and it must interrupt siblings without any per-stage opt-in.
// Branch output is compared too: declarative Jenkins emits NO `[branch]` prefix.
pipeline {
    agent any
    options {
        parallelsAlwaysFailFast()
    }
    stages {
        stage('Fan out') {
            parallel {
                stage('quick-fail') {
                    steps {
                        sh 'echo from-quick; sleep 1; exit 3'
                    }
                }
                stage('interrupted') {
                    steps {
                        sh 'echo from-slow; sleep 45; echo late > late.txt'
                    }
                }
            }
        }
    }
}
