// FG-111 round 4 (Codex P1). Workspace freshness and build HISTORY are
// independent: deleteDir() wipes the workspace, not the job's SCM build data.
// MEASURED: build 2 after a wipe gets the full CLONE shape (init, "Avoid
// second fetch", no `branch -D`) but ends `git rev-list --no-walk <prior sha>`
// — NOT "First time build". Fogell keeps the last-built revision
// controller-side (like the stash store) so it survives the wipe.
pipeline {
    agent any
    stages {
        stage('clone') {
            steps {
                git url: 'git://100.105.179.51/repo.git', branch: 'main'
            }
        }
    }
}
//// NEXT BUILD ////
pipeline {
    agent any
    stages {
        stage('wipe-then-clone') {
            steps {
                deleteDir()
                git url: 'git://100.105.179.51/repo.git', branch: 'main'
            }
        }
    }
}
