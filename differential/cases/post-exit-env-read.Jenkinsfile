// FG-202. A read AFTER a nested wrapper exits sees the RESTORED outer environment.
//
// The FG-201 fix made the refresh reach a nested wrapper's body; this is the shape it
// newly exposed, constructed by the pre-push verifier refuting that fix and then
// MEASURED: the entry-refresh's snapshot survived the wrapper's exit, so `${env.A}`
// after the block read the inner value where Jenkins restores the outer — a wrong
// value under a green build. The exit re-refresh reads the host's overlay again when
// a block-taking step returns.
//
// NESTING IS THE POINT, per the FG-202 row: an un-nested wrapper's minted binding
// does not survive its body, so only a read inside an ENCLOSING wrapper's body can
// meet a retained cell.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    dir('s') {
                        withEnv(['A=one']) { sh 'true' }
                        echo "after:${env.A}"
                    }
                }
            }
        }
    }
}
