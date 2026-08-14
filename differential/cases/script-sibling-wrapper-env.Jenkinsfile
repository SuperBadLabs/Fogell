// FG-179. A closure-local `def env` must not disturb a LATER, SIBLING wrapper.
//
// `env` provenance was one interpreter-wide bit, set the moment the interpreter noticed a
// `def env` anywhere. A closure that bound its own `env` therefore switched the wrapper
// refresh off FOR THE REST OF THE RUN, and every later `withEnv` body read a stale
// environment: Jenkins writes `deploy:prod`, Fogell wrote `deploy:null`.
//
// The two wrappers are SIBLINGS, not nested, which is the whole point — nothing about the
// first is in scope for the second, and only a global flag could carry the damage across.
// Provenance is now the identity of the cell each frame installed, so the question cannot
// be asked across frames at all.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    [1].each { def env = [TARGET: 'local'] }
                    withEnv(['TARGET=prod']) {
                        sh "printf 'deploy:%s' \"${env.TARGET}\" > deploy.txt"
                    }
                }
            }
        }
    }
}
