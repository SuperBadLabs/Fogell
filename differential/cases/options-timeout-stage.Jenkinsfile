// FG-045. `timeout` is not only a STEP — the corpus also declares it as a pipeline or
// stage OPTION, and stage options were being discarded by the parser outright.
//
// MEASURED: Jenkins ABORTS when an options timeout expires; `finished.txt` and any
// following stage never appear. Fogell ignored options entirely, so this pipeline ran the
// full 60-second sleep UNBOUNDED and reported success.
pipeline {
    agent any
    stages {
        stage('bounded') {
            options {
                timeout(time: 4, unit: 'SECONDS')
            }
            steps {
                sh 'echo started > started.txt; sleep 60; echo finished > finished.txt'
            }
        }
    }
}
