// FG-041b review fix, PR #14 round 4. Groovy interpolates a DOUBLE-quoted value and
// keeps a SINGLE-quoted one verbatim. The parser discarded the quote form, so a
// single-quoted value was expanded anyway — running a different value than Jenkins
// and risking a FALSE differential match, the one outcome this harness must never
// produce. Measured 53 single-quoted dollar-bearing assignments in the corpus.
//
// Uses `echo`, not `printf` with an embedded newline: a command containing a literal
// newline makes Jenkins' `set -x` trace span lines and only the first is
// recognisable as trace (KNOWN GAP in the comparison contract, FG-002c).
pipeline {
    agent any
    environment {
        SEED = 'seed-value'
        INTERPOLATED = "prefix-${SEED}"
        LITERAL = 'prefix-${SEED}'
        LITERAL_BARE = 'plain-$SEED'
    }
    stages {
        stage('Compare') {
            steps {
                sh 'echo "$INTERPOLATED" > interpolated.txt'
                sh 'echo "$LITERAL" > literal.txt'
                sh 'echo "$LITERAL_BARE" > literal_bare.txt'
            }
        }
    }
}
