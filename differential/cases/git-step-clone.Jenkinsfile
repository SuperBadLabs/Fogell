// FG-111/FG-052. The `git` step's FRESH-clone shape: the 20-line measured
// narration ending "First time build. Skipping changelog.", a real checkout
// (the sh step reads a file only the clone can provide), and a workspace hash
// over the checked-out tree (.git excluded on both sides). The
// `> git --version # '...'` line folds to ${GITVERSION} — each engine prints
// its own, the environment-of-necessity class.
pipeline {
    agent any
    stages {
        stage('clone') {
            steps {
                git url: 'git://100.105.179.51/repo.git', branch: 'main'
                sh 'cat src/a.txt'
            }
        }
    }
}
