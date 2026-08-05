// FG-134, second half. A `;` must terminate a RAW (unquoted) argument, not just
// separate two quoted steps.
//
// The step-block separator loop alone was not enough: for a step with named or
// unquoted arguments the `;` never reached that loop, because the raw argument
// scanners consumed it INSIDE the argument value. `sh script: 'a', label: 'x'; sh 'b'`
// captured the semicolon and everything after it as part of `label`.
//
// The QUOTED form (`sh 'a'; sh 'b'`) worked throughout, which is exactly why the
// first fix looked complete — that was the shape in front of me. Raised as P1
// independently by BOTH reviewers on PR #40.
pipeline {
    agent any
    stages {
        stage('one') {
            steps {
                sh script: 'echo a > a.txt', label: 'first'; sh 'echo b > b.txt'
            }
        }
        stage('two') {
            steps {
                echo 'unquoted step arg'; sh 'echo c > c.txt'
            }
        }
    }
}
