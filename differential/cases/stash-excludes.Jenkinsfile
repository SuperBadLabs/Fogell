// FG-047 review fix, PR #15 round 4. `excludes:` was parsed nowhere and applied nowhere,
// so a stash quietly carried files the author had asked it to leave out. After the
// round-trip the workspace must contain keep.txt and NOT drop.log — the absence is the
// claim, and the workspace hash carries it.
pipeline {
    agent any
    stages {
        stage('Produce') {
            steps {
                sh 'mkdir -p out; echo keep > out/keep.txt; echo drop > out/drop.log'
                stash name: 'filtered', includes: 'out/**', excludes: 'out/*.log'
            }
        }
        stage('Restore') {
            steps {
                deleteDir()
                unstash 'filtered'
                sh 'ls out > listing.txt'
            }
        }
    }
}
