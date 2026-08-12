// FG-184. A closure passed as an ARGUMENT to a step that takes no block is a second
// positional argument, and Jenkins rejects the call.
//
// THIS IS THE PUREST FALSE SUCCESS ON THIS BRANCH, which is why it is a case and not a
// note. Measured: Jenkins FAILS the build with an EMPTY workspace; Fogell reported
// SUCCESS and wrote `ran.txt`. Not a divergent value, not a divergent result — a green
// build that performed work Jenkins refused to start. ADR 0001 names this the outcome
// worse than any rejection.
//
// WHY THE EXISTING GUARDS ALL MISSED IT, in order, because each one looks like it should
// have caught this:
//   - the static block scan reads `body` as an `EVar`. It is only a closure at RUNTIME,
//     so no scan of the source can see it. This is the argument for the rule living in
//     the interpreter's normalisation rather than in another pre-flight arm.
//   - the arity default-deny (FG-177) refuses a two-positional `sh` — but never saw two.
//     The normalisation had already stripped the closure into a hosted body.
//   - `sh`'s dispatcher ignores a hosted body, so the stripping was silent. A wrapper
//     would at least have run the block.
// Three correct rules, and the value never reached any of them.
//
// `def body = {}` — a SEPARATELY BOUND closure, not an inline one, and that is the case
// working. Written inline as `sh('touch ran.txt') { }` the parser produces a trailing
// block and the existing guards do fire. The variable is what defers the closure to
// runtime, and runtime is where the hole was.
//
// The empty body matters too: a body that itself ran a step would fail the build here
// for the WRONG REASON and the case would pass against the defect.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def body = {}
                    sh('touch ran.txt', body)
                }
            }
        }
    }
}
