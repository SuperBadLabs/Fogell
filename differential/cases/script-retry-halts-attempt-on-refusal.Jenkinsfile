// FG-182. A hosted call Jenkins REFUSES must halt the attempt it is running in, not
// merely the script that contains it.
//
// `sh('invalid', 'extra')` is a two-positional call, which Jenkins rejects — the closure
// stops there and the workspace stays EMPTY. Fogell refused the call correctly and then
// ran the NEXT one anyway, writing a file from a body Jenkins had already abandoned.
//
// THE MECHANISM IS A SCOPE, WHICH IS WHY A CASE PINS IT RATHER THAN A UNIT TEST.
// `runWithRetry` gives each attempt a fresh `Failed` ref and a throwaway status sink,
// deliberately: a body that fails once and then succeeds is a SUCCESS build (FG-035,
// receipt `retry-succeeds`). The script host's refusal path marked the SCRIPT's context
// instead of the attempt's, so the outer flag was set, the attempt still looked clean to
// `halted`, and the second call ran. Both facts are needed to see it, and only a body
// running INSIDE a retry shows them at once.
//
// `retry(1)` — ONE attempt, not two, and that is the assertion working. With a second
// attempt the case would still diverge, but it would also be asserting the retry loop's
// behaviour on a deterministic refusal, and a case that measures two things at once
// tells you less about each. One attempt isolates the scope.
//
// THE WORKSPACE IS THE ASSERTION. Both engines FAIL this build, so a result-only
// comparison passes an engine that ran the wrong steps — the same shape as FG-181. The
// marker file is what separates them: absent on Jenkins, present on the defect.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    retry(1) {
                        sh('invalid', 'extra')
                        sh 'touch ran-after-invalid.txt'
                    }
                }
            }
        }
    }
}
