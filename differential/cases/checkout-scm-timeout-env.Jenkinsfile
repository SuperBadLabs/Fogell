//// SCM JOB ////
// FG-052 round 4. Two measurements in one case:
// * a top-level options{timeout} does NOT bound the Declarative auto-checkout —
//   the "Timeout set to expire" banner prints AFTER the checkout block;
// * the auto-checkout wraps every user stage in GIT_COMMIT (full sha),
//   GIT_BRANCH (origin/-prefixed), and GIT_URL — the withEnv wrapper the
//   measured console shows around the stages.
pipeline {
    agent any
    options { timeout(time: 2, unit: 'MINUTES') }
    stages {
        stage('env') {
            steps {
                sh 'echo commit=$GIT_COMMIT'
                sh 'echo branch=$GIT_BRANCH'
                sh 'echo url=$GIT_URL'
            }
        }
    }
}
