//// SCM JOB ////
// FG-052 round 2. `options { skipDefaultCheckout() }` suppresses the
// Declarative auto-checkout stage; the Obtained line still prints and the
// explicit `checkout scm` then does the build's FIRST checkout (fresh clone
// shape, First-time tail — nothing checked out before it).
pipeline {
    agent any
    options { skipDefaultCheckout() }
    stages {
        stage('co') {
            steps {
                checkout scm
                sh 'cat src/a.txt'
            }
        }
    }
}
