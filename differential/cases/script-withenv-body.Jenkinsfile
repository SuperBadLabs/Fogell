// FG-172. `withEnv` inside a `script { }`, via TYPED arguments through the bridge.
//
// The host used to build a `Step` by rendering interpreter values to display text, so a
// Groovy list `['A=1']` arrived as the string `[A=1]` — unquoted — and the arm's parser,
// which matches QUOTED list elements, bound nothing while the build reported success.
// `Step` still holds strings (ADR 0002); the evaluated form rides on `BranchCtx` instead,
// where the interpreter is already a dependency.
//
// The second `sh` is the assertion that matters: `out:` must be EMPTY. `withEnv` is
// block-scoped on Jenkins, so a bridge that leaked the binding past the block would print
// `out:inside` and a case checking only the first line would call that a pass.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    withEnv(['SCRIPT_SCOPED=inside']) {
                        sh 'echo in:$SCRIPT_SCOPED'
                    }
                    sh 'echo out:$SCRIPT_SCOPED'
                }
            }
        }
    }
}
