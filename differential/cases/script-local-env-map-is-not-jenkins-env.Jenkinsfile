// FG-179. A script's own `def env = [:]` is an ordinary map. Only the JENKINS environment
// goes to the host.
//
// The routing was syntactic: any `env.FOO = …` was sent to `host.SetEnv`, which refuses
// because Fogell's environment overlay does not cross the script boundary. So a local map
// that merely shared the NAME had its ordinary mutation refused, and the step after it
// never ran — Jenkins writes the file and carries on.
//
// Provenance is now the CELL'S IDENTITY: the `env` the caller supplied is Jenkins',
// anything the script binds later is its own, and no syntactic form has to be enumerated.
//
// `ok.txt` is the assertion, and it is deliberately AFTER the assignment: the defect was
// never about the map's contents, it was that the refusal killed everything downstream.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def env = [:]
                    env.FOO = 'local'
                    sh 'touch ok.txt'
                }
            }
        }
    }
}
