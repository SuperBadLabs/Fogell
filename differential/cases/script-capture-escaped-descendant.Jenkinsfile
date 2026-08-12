// FG-181. A descendant that ESCAPES the process group holds the inherited stdout write
// end open after the shell itself has exited. The captured value must still be the bytes
// the shell wrote.
//
// WHY THIS CASE EXISTS RATHER THAN A UNIT TEST. The defect it pins was a WRONG VALUE
// UNDER A GREEN BUILD — both engines reported success, and the only difference was what
// `out` contained. A result-only comparison passes that, which is why it survived a
// review round and a full suite: `sh(returnStdout: true)` waited five seconds for a read
// that could not complete, and the fallback substituted "" for bytes that had ALREADY
// ARRIVED. Jenkins returned `token`; Fogell returned nothing and carried on. ADR 0001's
// worst class, produced by a fallback added to fix an earlier, louder version of itself.
//
// `setsid` is what makes the case bite. Without it the child is in the step's process
// group and is reaped with it, the pipe closes, and the read completes normally — which
// is why every existing capture receipt passed throughout. The escape is deliberate and
// supported: ADR 0008 says the group is LIFECYCLE containment, not a hostile boundary,
// and a workload may leave it with its own `setsid`. So this is not an exotic input; it
// is the documented way out, exercised.
//
// The sleep OUTLIVES the capture bound on purpose. Ten seconds against a five-second
// wait means the read is still open when the bound expires on every run, on any host —
// a shorter sleep would make the case pass for the wrong reason on a slow one, and a
// case that can pass for the wrong reason is not evidence. The cost is one bounded wait
// per suite run, paid once.
//
// BOTH SHAPES ARE ASSERTED because they fail differently:
//   - `raw:[token]` on the console pins the value the script actually received. An
//     engine that erases the capture prints `raw:[]`, which is the measured defect.
//   - `captured.txt` puts the same value in the WORKSPACE HASH, so an engine that
//     narrated the right text while handing the script something else still diverges.
pipeline {
    agent any
    stages {
        stage('Capture') {
            steps {
                script {
                    def out = sh(script: 'setsid sleep 10 & printf token', returnStdout: true)
                    echo "raw:[${out}]"
                    sh "printf 'captured=%s' '${out}' > captured.txt"
                }
            }
        }
    }
}
