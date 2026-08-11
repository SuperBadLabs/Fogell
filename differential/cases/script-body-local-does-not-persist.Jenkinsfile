// FG-178. A name the body DECLARES does not survive the invocation; a name it CAPTURED
// does. Both halves in one case, because a fix for either alone breaks the other.
//
// I recorded this exact gap as a KNOWN LIMIT when the captured-locals fix landed —
// carrying the whole environment forward meant a `def` inside the body persisted where
// Groovy creates it afresh. Review then MEASURED it: on Jenkins the second attempt cannot
// see `marker` and the build FAILS; Fogell succeeded and wrote the file. Writing a limit
// down makes it honest, not acceptable.
//
// THE CASE EXERCISES BOTH DIRECTIONS AT ONCE, which is what makes it worth keeping:
//   `n` is CAPTURED and must persist, or `retry` cannot make progress and attempt 2
//     repeats attempt 1 — the defect `script-retry-keeps-locals` closed.
//   `marker` is DECLARED INSIDE and must NOT persist, or attempt 2 reads a value Jenkins
//     has already discarded.
// A fix that keeps everything passes the first and fails the second; one that keeps
// nothing does the reverse. Only the pair pins it.
//
// The split needs no scope machinery: present BEFORE the invocation means captured,
// present only after means declared here. Proper block scoping — `def` inside `if`, and
// the rest — is FG-179's variable model, and this does not pretend to be it.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def n = 0
                    retry(2) {
                        n = n + 1
                        if (n == 1) {
                            def marker = 'seen'
                            sh 'exit 1'
                        }
                        sh "printf saw:${marker} > marker.txt"
                    }
                    sh 'echo after > after.txt'
                }
            }
        }
    }
}
