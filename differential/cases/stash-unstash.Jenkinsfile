// FG-047. `stash` / `unstash`, and the property that matters: a stash is stored with
// the BUILD, not in the workspace, so it survives `deleteDir()`. Storing it under the
// workspace would pass a naive round-trip test and fail this one.
pipeline {
    agent any
    stages {
        stage('Produce') {
            steps {
                sh 'mkdir -p out; echo artifact-body > out/thing.txt'
                stash name: 'built', includes: 'out/**'
            }
        }
        stage('Wipe and restore') {
            steps {
                deleteDir()
                sh 'ls out 2>/dev/null | wc -l > before.txt'
                unstash 'built'
                sh 'cat out/thing.txt > restored.txt'
            }
        }
    }
}
