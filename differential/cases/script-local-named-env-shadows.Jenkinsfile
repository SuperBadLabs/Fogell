// FG-178. A local NAMED `env` shadows the Jenkins environment, like any other local.
//
// The third case in this family, and the second regression I introduced fixing the one
// before it. Removing the bare-name clobbering left exactly one binding still overwritten
// unconditionally — `env` itself — and `def env = [TARGET: 'local']` is legal Groovy that
// Jenkins honours: it prints `local`, and Fogell printed `wrapper`.
//
// THE FIRST ATTEMPT AT THIS FIX FAILED, and the reason is the point. It seeded provenance
// from the CALL SITE, where `def env = …` has already run — so the shadowed value was
// compared against itself and always looked unshadowed. The original binding is only
// visible where the SCRIPT started, so provenance is anchored on the interpreter state
// rather than beside each thunk. Measured before and after, not reasoned.
//
// Read this case with `script-withenv-body-sees-env` (the wrapper's override must still
// reach `env.TARGET`) and `script-local-shadows-wrapper-env` (a bare local must survive).
// Each of the three passes a version that breaks one of the others; only together do they
// pin the rule, which is "never overwrite a user binding, always refresh our own".
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def env = [TARGET: 'local']
                    withEnv(['TARGET=wrapper']) {
                        sh "printf ${env.TARGET} > marker.txt"
                    }
                }
            }
        }
    }
}
