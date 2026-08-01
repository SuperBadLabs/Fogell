// FG-111/FG-052. The `git` step's EXISTING-repo shape, reachable only through
// the FG-110 sequence lane: build 2 re-fetches into the workspace build 1
// cloned — "Fetching changes from the remote Git repository", `git branch -D`
// before the re-branch, and `git rev-list --no-walk <pre-fetch HEAD>` as the
// last line. These receipts seal the UNCHANGED-remote variant; a probe with a
// commit pushed between builds measured the console structurally identical
// (the changelog is computed, never printed by this step) — that variant is
// measurement, not yet a sealed receipt.
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
        stage('refetch') {
            steps {
                git url: 'git://100.105.179.51/repo.git', branch: 'main'
            }
        }
    }
}
