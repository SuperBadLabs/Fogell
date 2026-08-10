// FG-174. A hosted `timeout` with TWO positional arguments is a signature error, and
// Jenkins raises it at RUNTIME rather than refusing the model.
//
// MEASURED on the pinned lab: `timeout(1, 2)` gives `IllegalArgumentException: Expected
// named arguments but got [1, 2]`, and the EARLIER STAGE HAS ALREADY RUN when it does —
// which is what decides where the refusal belongs. A duplicated named argument is a
// COMPILE-time rejection and is refused in the parser; this one is a runtime shape
// error and is refused at dispatch. Same class of defect, opposite placement, and
// getting that backwards would diverge on everything before the failing stage.
//
// Fogell read the FIRST positional as one minute, silently DROPPED the `2`, ran the body
// and reported SUCCESS — the ninth finding of the hosted-signature class, on the one
// wrapper that had no case in `hostedSignatureError`.
//
// `before.txt` is the assertion that matters: it must exist on BOTH engines, proving the
// refusal lands at the timeout call and not earlier. `inside.txt` and `after.txt` must be
// ABSENT on both — an engine that ran the body leaves them behind, and the terminal
// result alone would not show which of the three files decided the verdict.
pipeline {
    agent any
    stages {
        stage('Before') {
            steps { sh 'echo b > before.txt' }
        }
        stage('Gated') {
            steps {
                script {
                    timeout(1, 2) {
                        sh 'echo inside > inside.txt'
                    }
                    sh 'echo after > after.txt'
                }
            }
        }
    }
}
