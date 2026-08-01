// FG-110 round 1 (Codex P1). A stash is scoped to the BUILD that saved it:
// build 2's unstash of build 1's stash must FAIL on both engines. Until this
// case, Fogell keyed stashes by job alone, so a sequence's build 2 could
// silently read build 1's files where Jenkins reports the stash missing.
pipeline {
    agent any
    stages {
        stage('save') {
            steps {
                sh 'echo payload > stashed.txt'
                stash name: 'carried', includes: 'stashed.txt'
            }
        }
    }
}
//// NEXT BUILD ////
pipeline {
    agent any
    stages {
        stage('try-restore') {
            steps {
                unstash 'carried'
            }
        }
    }
}
