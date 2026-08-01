// FG-111. The branchless positional form — `git 'url'` — that 13 of the 228
// corpus files use. MEASURED: Jenkins defaults the branch to `master`
// (rev-parse refs/remotes/origin/master, re-branch as master). The fixture
// repo carries a master ref for exactly this case.
pipeline {
    agent any
    stages {
        stage('clone') {
            steps {
                git 'git://100.105.179.51/repo.git'
            }
        }
    }
}
