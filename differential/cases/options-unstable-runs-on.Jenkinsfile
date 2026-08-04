// FG-053(b). THE CONTROL for `options-skip-after-unstable`: the same pipeline
// with the option removed, so the following stage RUNS and the build is still
// `unstable`.
//
// It also pins `unstable('msg')` itself, which had to be implemented before
// `skipStagesAfterUnstable` could be reached at all: this engine previously had
// NO way to make a build unstable from inside a pipeline. MEASURED — it prints
// `WARNING: msg`, sets the result, and EXECUTION CONTINUES through the rest of
// the stage and into later stages.
//
// Without this control the option case diverged for a reason that had nothing to
// do with the option, and it was this pair that showed it.
pipeline {
    agent any
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
