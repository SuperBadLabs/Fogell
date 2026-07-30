// FG-041b review fix, PR #14 round 9. `"\\$SEED"` is an escaped BACKSLASH followed
// by a LIVE interpolation: Groovy yields one backslash then expands $SEED. Decoding
// the escaped dollar to `\$` made that indistinguishable from an originally escaped
// dollar, so this value came out as a literal `$SEED`. A NUL sentinel — impossible in
// an environment value — keeps the two cases apart.
pipeline {
    agent any
    environment {
        SEED = 'seed'
        ESCAPED_DOLLAR = "keep-\$SEED"
        ESCAPED_BACKSLASH = "slash-\\$SEED"
    }
    stages {
        stage('Compare') {
            steps {
                sh 'echo "$ESCAPED_DOLLAR" > dollar.txt'
                sh 'echo "$ESCAPED_BACKSLASH" > backslash.txt'
            }
        }
    }
}
