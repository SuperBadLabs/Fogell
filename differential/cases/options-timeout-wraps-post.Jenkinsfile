// FG-045 review fix, PR #16 round 5. A pipeline-level timeout wraps the POST block too.
// The deadline reached the stage walk but not the pipeline post, so a slow
// `post { always { … } }` ran unbounded past a timeout Jenkins enforces around it.
// `late.txt` must be absent on both engines.
pipeline {
    agent any
    options {
        timeout(time: 5, unit: 'SECONDS')
    }
    stages {
        stage('quick') {
            steps {
                sh 'echo quick > quick.txt'
            }
        }
    }
    post {
        always {
            sh 'echo entered > entered.txt; sleep 60; echo late > late.txt'
        }
    }
}
