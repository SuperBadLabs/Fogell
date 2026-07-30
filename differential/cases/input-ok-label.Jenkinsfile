// FG-046 review fix, PR #17. The confirmation label is configurable. MEASURED:
// `ok: 'Ship it'` makes Jenkins print "Ship it or Abort"; hardcoding "Proceed" diverged on
// any pipeline that customises it.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                timeout(time: 4, unit: 'SECONDS') {
                    input message: 'Deploy to prod?', ok: 'Ship it'
                }
            }
        }
    }
}
