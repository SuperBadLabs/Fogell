// FG-174. `script` takes NO arguments — only its implicit closure.
//
// The walker's arm guarded on `ScriptBody` alone, so `script('ignored') { … }` ran the
// body while Jenkins rejects the call and leaves the workspace EMPTY. Measured. The body
// RUNNING is the defect, not the ignored argument: side effects from a pipeline Jenkins
// never started, reported as success.
//
// `ran.txt` must be ABSENT on both engines. Comparing the terminal result alone would
// not distinguish an engine that ran the body and then failed for some other reason.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script('ignored') {
                    sh 'echo ran > ran.txt'
                }
            }
        }
    }
}
