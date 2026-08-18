// FG-202, the ABNORMAL exit. A wrapper body that FAULTS — rather than returning —
// still restores the enclosing environment for the read after it.
//
// Constructed by the pre-push verifier against the first spelling of the exit
// refresh, which ran only when the wrapper RETURNED: a divide-by-zero inside the
// body leaves `withEnv` by exception, the script-level catch swallows it, and the
// read after the block met the retained inner snapshot. The refresh now runs in a
// finally, so both exits restore. The sibling case `post-exit-env-read` covers the
// plain-return exit; this one exists because a green run of that case says nothing
// about this path.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    dir('s') {
                        try { withEnv(['A=one']) { def x = 1 / 0 } } catch (e) { }
                        echo "after:${env.A}"
                    }
                }
            }
        }
    }
}
