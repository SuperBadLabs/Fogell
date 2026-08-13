// FG-172. A hosted `timeout` must BOUND its body, not merely announce a budget. The
// deadline normally travels as a dispatch argument, and a hosted body's inner steps are
// dispatched by the script host — which has no way to be told — so without
// `HostedDeadline` this slept the full 30s while printing "Timeout set to expire in 3
// seconds". A safety bound defeated ranks with a bypassed approval here.
//
// `done.txt` is the assertion: it must be ABSENT on both engines. Comparing only the
// terminal result would pass an engine that let the sleep finish and then failed.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    timeout(time: 3, unit: 'SECONDS') {
                        sh 'sleep 30; echo never > done.txt'
                    }
                }
            }
        }
    }
}
