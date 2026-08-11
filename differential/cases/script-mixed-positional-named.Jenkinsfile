// FG-177. POSITIONAL **OR** NAMED, NEVER BOTH — a CPS-level rule, not a per-step one.
//
// Jenkins' `DSL.parseArgs` throws `Expected named arguments but got …` whenever a named
// map arrives beside positional arguments, whatever the step. Fogell admitted the shape
// and ran the call.
//
// MEASURED ON TWO STEPS before generalising, because the previous version of this rule
// was written as a `timeout`-only arm and that narrowness WAS the fifteenth finding of
// the class — the right rule in the wrong scope. `sh('exit 7', returnStatus: true)` and
// `archiveArtifacts('*.txt', fingerprint: true)` both make Jenkins fail leaving ONLY the
// earlier stage's file, with the same workspace hash. Two steps, one behaviour: the rule
// belongs above the per-step match, and `timeout`'s own mixed branch was deleted rather
// than left as an unreachable second opinion.
//
// `marked.txt` IS THE ASSERTION. The call returns a status, so an engine that admits the
// shape takes the `code == 7` branch and writes it — proving not merely that the call
// ran, but that a GUARDED follow-up ran off a value Jenkins never produced. `before.txt`
// must survive on both engines, placing the refusal at the call and not earlier.
pipeline {
    agent any
    stages {
        stage('Prep') {
            steps { sh 'echo before > before.txt' }
        }
        stage('Mixed') {
            steps {
                script {
                    def code = sh('exit 7', returnStatus: true)
                    if (code == 7) {
                        sh 'echo marked > marked.txt'
                    }
                }
            }
        }
    }
}
