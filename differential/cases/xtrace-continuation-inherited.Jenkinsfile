// FG-102 round 48. A multiline word in a traced command: dash prints `+ ` on the
// FIRST physical trace line only, so $HOME's expansion lands on a BARE
// continuation row that per-line normalisation cannot attribute. The engines
// inherit different HOMEs by construction; the compare-time two-sided resolution
// is what makes this case PROVEN instead of a false divergence.
pipeline {
    agent any
    stages {
        stage('continuation') {
            steps {
                sh '''printf '%s' "head
$HOME/tail" >/dev/null
echo done'''
            }
        }
    }
}
