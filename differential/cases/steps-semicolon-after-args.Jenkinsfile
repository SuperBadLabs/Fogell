// FG-134, second half. A `;` must terminate a RAW (unquoted) argument, not just
// separate two quoted steps.
//
// The step-block separator loop alone was not enough: for a step with an
// unquoted argument the `;` never reached that loop, because the raw argument
// scanners consumed it INSIDE the value.
//
// THE ARGUMENT ADJACENT TO EACH `;` IS GENUINELY RAW — `returnStatus: true`,
// unquoted — and that is the whole point. An earlier version used `label: 'first'`
// and `echo 'unquoted step arg'`, both QUOTED, so it would have passed unchanged
// while the raw scanners still swallowed the semicolon: it proved the separator
// loop and CLAIMED to prove argument termination. Caught by the pre-push verifier
// reading the case against its own comment.
//
// `sleep 1` was tried here first and is not implemented by this engine, so the
// case failed for a reason that had nothing to do with semicolons — the control
// (same steps, no semicolons) is what separated the two.
pipeline {
    agent any
    stages {
        stage('one') {
            steps {
                sh script: 'echo a > a.txt', returnStatus: true; sh 'echo b > b.txt'
            }
        }
        stage('two') {
            steps {
                sh script: 'echo c > c.txt', returnStatus: true; sh 'echo d > d.txt'
            }
        }
    }
}
