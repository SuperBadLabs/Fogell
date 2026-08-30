// FG-044b(d). Stash applies Ant's default excludes unless the exact
// useDefaultExcludes:false opt-out is present. A .git descendant is the
// representative oracle case; the Execution suite covers the full default set.
// `.git/**` is excluded from canonical workspace hashes, so the compared shell
// assertions/xtrace and fixed markers are the load-bearing witnesses here.
pipeline {
    agent any
    stages {
        stage('Save') {
            steps {
                sh 'mkdir -p .git; printf visible > visible.txt; printf hidden > .git/hidden.txt'
                stash name: 'defaults-on', includes: '**'
                stash name: 'defaults-off', includes: '**', useDefaultExcludes: false
            }
        }
        stage('Restore default') {
            steps {
                deleteDir()
                unstash 'defaults-on'
                sh 'test -f visible.txt && test ! -e .git/hidden.txt && printf excluded > default.txt'
            }
        }
        stage('Restore opt-out') {
            steps {
                deleteDir()
                unstash 'defaults-off'
                sh 'test -f visible.txt && test -f .git/hidden.txt && printf included > opt-out.txt'
            }
        }
    }
}
