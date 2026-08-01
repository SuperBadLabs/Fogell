//// SCM JOB ////
// FG-052 round 5. The wrapper env's LAYER, measured: a pipeline-declared
// GIT_COMMIT overrides the auto-checkout's value for user steps (declarations
// apply INSIDE the wrapper), while `when { environment name: 'GIT_BRANCH' }`
// sees the wrapper's origin/-prefixed value and runs the stage.
pipeline {
    agent any
    environment { GIT_COMMIT = 'declared-wins' }
    stages {
        stage('layered') {
            when { environment name: 'GIT_BRANCH', value: 'origin/case/checkout-scm-env-layering' }
            steps {
                sh 'echo commit=$GIT_COMMIT'
            }
        }
    }
}
