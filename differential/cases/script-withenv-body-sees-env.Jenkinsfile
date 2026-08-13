// FG-178. A wrapper's environment must be visible to its body's EVALUATION, not only
// to the steps the body dispatches.
//
// MEASURED before the fix: Jenkins printed `saw:prod`, Fogell printed **`saw:null`**.
// FG-172 delivered the wrapper's context to the body's STEPS — cwd and the env overlay a
// step runs under — and that looked like the whole job. It was half of it: the
// interpreter's `env` binding is built ONCE before `runHosted`, so the body's Groovy read
// a PRE-WRAPPER snapshot and interpolated the old value into the command.
//
// The fix is a `CurrentEnv` callback, asked at each invocation rather than captured, and
// BOTH spellings are rebound — a bare `TARGET` and `env.TARGET`. Refreshing only the map
// would fix this case and leave the bare name stale, which is half a fix that looks whole.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    withEnv(['TARGET=prod']) {
                        sh "echo saw:${env.TARGET} > saw.txt"
                    }
                }
            }
        }
    }
}
