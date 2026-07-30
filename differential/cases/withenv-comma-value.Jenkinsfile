// FG-041b review fix. A value containing a comma must survive. Splitting the raw
// list on every comma bound `CSV=a` where Jenkins exposes `a,b,c`. Caught by a
// Codex review comment on PR #13, and the file content is the evidence.
//
// Deliberately avoids a literal `\n` in the command: Jenkins' `set -x` trace of
// such a command spans multiple lines and only the first is recognisable as
// trace, so the continuation would be compared as build output (see the KNOWN GAP
// in the comparison contract, FG-002c). The claim lives in the workspace hash.
pipeline {
    agent any
    stages {
        stage('Work') {
            steps {
                withEnv(['CSV=a,b,c', 'PLAIN=x']) {
                    sh 'echo "$CSV" > csv.txt; echo "$PLAIN" > plain.txt'
                }
            }
        }
    }
}
