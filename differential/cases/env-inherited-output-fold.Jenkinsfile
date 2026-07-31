// FG-102 round 48 (P1). Ordinary stdout printing an inherited value: the trace
// rows are byte-equal, the output pair differs only by the engines' HOMEs —
// which differ by construction of the harness, not by engine behaviour (on one
// agent they could not differ). The pair folds to ${HOME} under the declared
// environment-of-necessity rule, and the receipt LISTS the folded pair, so the
// relaxation this verdict used is visible in this file, not only in the rule.
pipeline {
    agent any
    stages {
        stage('fold') {
            steps {
                sh 'printenv HOME'
            }
        }
    }
}
