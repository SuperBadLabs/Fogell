// FG-053(b). `options { skipStagesAfterUnstable() }` stops the build at the
// first stage that marked it UNSTABLE.
//
// MEASURED on Jenkins 2.568.1, and paired with `options-unstable-runs-on`, which
// is the SAME pipeline without the option. That pairing is what makes this a
// measurement of the OPTION rather than of unstable handling in general:
//   with:    Stage "three" skipped due to earlier stage(s) marking the build as unstable
//   without: + echo three
// Both end `unstable` and both run pipeline `post`.
//
// The skip has its OWN sentence — not the `due to earlier failure(s)` one — and
// `three.txt` is ABSENT from the workspace, so the hash checks the stage really
// did not run rather than that a line was printed.
pipeline {
    agent any
    options { skipStagesAfterUnstable() }
    stages {
        stage('one') { steps { sh 'echo one > one.txt' } }
        stage('makes-unstable') {
            steps {
                sh 'echo u > u.txt'
                unstable('flaky')
            }
        }
        stage('three') { steps { sh 'echo three > three.txt' } }
    }
    post { always { sh 'echo post > post.txt' } }
}
