// FG-177 slice 2. A sole required parameter may be written BY NAME: `dir(path: 'sub')`
// is the same call as `dir('sub')`, and Fogell refused it. A FALSE REFUSAL — measured,
// Jenkins runs the body and succeeds where Fogell failed with an EMPTY workspace.
//
// The arms accepted only the positional spelling, so the fix is not another arm: the
// sole required parameter's NAME is now data (`WalkerRules.soleRequiredParameter`) and
// the named form is normalised into the positional one ONCE, before validation and
// before dispatch. No arm learns two spellings.
//
// TWO FILES, because the point is that the wrapper still WORKS after normalisation, not
// merely that the call is admitted: `where.txt` inside `sub/` proves the directory was
// established, `top.txt` at the root proves it was restored. Only the workspace PATHS
// show that — the contents are identical either way, which is how the original `dir`
// defect hid.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    dir(path: 'sub') {
                        sh 'echo inside > where.txt'
                    }
                    sh 'echo top > top.txt'
                }
            }
        }
    }
}
