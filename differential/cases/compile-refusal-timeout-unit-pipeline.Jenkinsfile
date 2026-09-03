// FG-240/FG-129. A pipeline-level `timeout` with an unusable unit. The pinned
// reference compiler refuses the model — MEASURED on Jenkins 2.568.1
// (2026-09-03): `Expecting "class java.util.concurrent.TimeUnit" for parameter
// "unit" but got "NOPE"`, nothing runs. Fogell refuses the same model before
// the SCM block (FG-240). The receipt compares the typed refusal disposition,
// terminal result and workspace hash; compiler wording is outside the
// compatibility claim (FG-129).
pipeline {
    agent any
    options { timeout(time: 1, unit: 'NOPE') }
    stages {
        stage('must-not-run') {
            steps {
                sh 'touch stage-marker.txt'
            }
        }
    }
    post {
        always {
            sh 'touch post-marker.txt'
        }
    }
}
