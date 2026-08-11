// FG-178. A LOCAL SHADOWS THE ENVIRONMENT, even inside a wrapper that overrides it.
//
// This case exists because I INTRODUCED the defect while fixing the one beside it. The
// `CurrentEnv` refresh first rebound both spellings — a bare `TARGET` and `env.TARGET` —
// reasoning that refreshing one and not the other is half a fix. Measured, that was
// wrong: folding the environment over the body's bindings CLOBBERS a captured local.
// Jenkins gives `local:staging` because the local shadows the environment; Fogell gave
// `local:prod`.
//
// BOTH FILES ARE THE ASSERTION, and one alone would hide the other: `local.txt` proves
// the local survived the wrapper, `env.txt` proves the wrapper's override still reached
// `env.TARGET`. The first fix passed an env-only case and broke locals; a case that
// checked only the local would now pass a version that never refreshed the environment
// at all.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def TARGET = 'staging'
                    withEnv(['TARGET=prod']) {
                        sh "printf local:${TARGET} > local.txt"
                        sh "printf env:${env.TARGET} > env.txt"
                    }
                }
            }
        }
    }
}
