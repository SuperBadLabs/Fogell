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
// `echo $HOME` expands to a path that differs between the two engines, and the shell
// expands it BEFORE tracing, so the value lands on the `+ ` XTRACE row — which is the
// only place the env fold applies. A first attempt used `printenv HOME`, whose value
// prints on an ordinary OUTPUT line: that is deliberately NOT folded, and the case
// diverged for real (jenkins=/var/jenkins_home fogell=/home/srikanth). Running it in
// PARALLEL forces both relaxations at once.
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
