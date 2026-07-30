// FG-046. `input` under a `timeout`. MEASURED: Jenkins prints the message and
// "Proceed or Abort", waits, and when the deadline expires the build is ABORTED — the
// step after the gate never runs, so `after.txt` must be absent while `before.txt` is
// present.
//
// 22 corpus files use `input`; 10 wrap it in a timeout like this. The un-timed form waits
// for a human forever on Jenkins, which no receipt can capture — see FG-046b.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                sh 'echo before > before.txt'
                timeout(time: 4, unit: 'SECONDS') {
                    input 'Proceed?'
                }
                sh 'echo after > after.txt'
            }
        }
    }
}
