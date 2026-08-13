// FG-177 slice 2. `withEnv(overrides: [...])` — the sole required parameter by name.
//
// A FALSE REFUSAL, measured: Jenkins binds the variable and succeeds, Fogell failed with
// an EMPTY workspace. This case is the one that proves normalisation reaches DISPATCH and
// not just validation: `withEnv`'s arm reads its typed LIST from the positional slot, so
// admitting the named spelling without rewriting it would have bound nothing and reported
// success — the exact silent-empty-binding defect FG-172 already fixed once for the
// display-string form.
//
// The second `sh` is the assertion that matters: `out:` must be EMPTY, because block
// scoping is what a leak would break, and a case reading only the first line would call
// that a pass.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    withEnv(overrides: ['SCOPED=inside']) {
                        sh 'echo in:$SCOPED > in.txt'
                    }
                    sh 'echo out:$SCOPED > out.txt'
                }
            }
        }
    }
}
