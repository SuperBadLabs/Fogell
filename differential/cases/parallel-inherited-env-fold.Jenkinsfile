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
// WHAT THIS EXERCISES, precisely: ORDINARY OUTPUT inherited-env folding under
// MULTISET comparison. The xtrace rows arrive already canonicalised — normalisation
// applies the trace-only replacements before comparison, so both sides read
// `+ echo ${HOME}` and never enter the fold path at all. The lines that fold are the
// ordinary output rows, `/var/jenkins_home` against `/home/srikanth`. The novel
// combination is fold + multiset together, which no other case had.
//
// TWO WRONG COMMENTS PRECEDED THIS ONE, both about the same mechanism. The first said
// ordinary output is "deliberately NOT folded, unlike xtrace rows" — the reverse of
// the truth, and `env-inherited-output-fold` already proved ordinary output folds. The
// second said `echo $HOME` folds on BOTH rows, which the receipt disproves. The real
// cause of the first draft's divergence was mine: invoking the differential CLI
// directly instead of through `run-differential.sh` left `FOGELL_JENKINS_ENV_CMD`
// unset, silently setting `envCanonicalisationEnabled` to FALSE.

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
