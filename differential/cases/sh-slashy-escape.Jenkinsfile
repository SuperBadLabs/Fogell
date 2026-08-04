// FG-125. What a SLASHY string does with a backslash sequence.
//
// A slashy literal is a GString, but unlike a double-quoted one it is claimed to
// escape ONLY its `/` delimiter and to preserve every other backslash sequence,
// so `/\033/` should reach the shell as a backslash and three digits rather than
// an ESC byte.
//
// That claim is what this case MEASURES. It was deferred once already because
// the obvious probe — `sh /printf '[\033]'/`, no parentheses — is REFUSED by
// Jenkins at compile time (`expecting '}', found '[]'`), which proved nothing
// about escapes and only proved the form was wrong. The PARENTHESISED call is
// the accepted spelling.
//
// `od -c` renders the bytes, so the receipt records what the shell actually
// received instead of leaving it to trace normalisation.
pipeline {
    agent any
    stages {
        stage('one') {
            steps {
                sh(/printf '[\033]' > slashy.txt/)
                sh 'od -c slashy.txt'
            }
        }
    }
}
