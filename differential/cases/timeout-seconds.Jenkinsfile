// FG-034. `timeout` with an explicit unit. Jenkins' DEFAULT unit is MINUTES,
// so an engine that assumes seconds would let this run 120x too long.
pipeline {
    agent any
    stages {
        stage('Bounded') {
            steps {
                timeout(time: 3, unit: 'SECONDS') {
                    sh 'echo starting > started.txt; sleep 60; echo finished > finished.txt'
                }
            }
        }
    }
}
