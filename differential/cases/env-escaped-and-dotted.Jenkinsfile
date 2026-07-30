// FG-041b review fixes, PR #14 rounds 7 and 8.
//   * "\$NAME" in a GString is the LITERAL text $NAME — Groovy does not interpolate
//     it. The parser stripped the backslash BEFORE the value was classified as
//     interpolating, so the pass expanded what Jenkins leaves alone.
//   * "$env.NAME" and "${env.NAME}" are the ordinary Jenkins spellings. The old
//     pattern matched only a bare identifier, so `$env` resolved to nothing and
//     `.NAME` was left behind, and the braced dotted form was not matched at all.
pipeline {
    agent any
    environment {
        SEED = 'seed'
        ESCAPED = "keep-\$SEED"
        DOTTED_BARE = "got-$env.SEED"
        DOTTED_BRACED = "got-${env.SEED}"
    }
    stages {
        stage('Compare') {
            steps {
                sh 'echo "$ESCAPED" > escaped.txt'
                sh 'echo "$DOTTED_BARE" > dotted_bare.txt'
                sh 'echo "$DOTTED_BRACED" > dotted_braced.txt'
            }
        }
    }
}
