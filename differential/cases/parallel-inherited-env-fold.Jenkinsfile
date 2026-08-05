// FG-158. A CONCURRENT case that also needs the inherited-env fold — the one
// combination the suite never had.
//
// The two relaxations are independent and were never exercised together: multiset
// comparison (concurrent) and canonical folding (inherited env). The concurrent
// branch DISCARDED its fold list and reported counts, so a parallel case needing a
// fold would have passed with the receipt never naming the line it resolved — while
// the contract promised "EVERY pair the rule folds is LISTED in the receipt that
// used it (sealed)". Found by the verifier; the fix keeps the list, and this case is
// what makes the fix visible rather than merely written.
//
// `echo $HOME` puts the differing path on both the `+ ` xtrace row and the output
// line, and BOTH fold — the contract says so explicitly: ordinary output that prints
// an inherited value (e.g. `printenv HOME`) folds the same way. Running it in PARALLEL
// forces multiset comparison and env folding at once.
//
// A first draft used `printenv HOME` and diverged, and I wrote the WRONG REASON into
// this comment: that output lines are deliberately not folded, unlike xtrace rows.
// They are folded. The real cause was mine — I invoked the differential CLI directly
// instead of through `run-differential.sh`, so `FOGELL_JENKINS_ENV_CMD` was unset and
// `envCanonicalisationEnabled` silently became FALSE. I had diagnosed that correctly
// at the time and still recorded the other explanation here, where it would have
// taught the next reader a rule the engine does not have.

pipeline {
    agent any
    stages {
        stage('fan') {
            parallel {
                stage('left') {
                    steps {
                        sh 'echo $HOME'
                    }
                }
                stage('right') {
                    steps {
                        sh 'echo $HOME'
                    }
                }
            }
        }
    }
}
