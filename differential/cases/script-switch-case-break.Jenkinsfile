// FG-183. A `switch` arm's `break` is ORDINARY GROOVY, and the pipeline must run.
//
// THIS CASE EXISTS BECAUSE THE FG-183 FIX NEARLY BROKE IT. Refusing a `break` with no
// enclosing loop is right for `dir('d') { break }`; the parser used to LOWER `switch` to
// nested ifs, so a perfectly ordinary `case 'a': break` arrived at that check looking
// identical and would have been refused — a pipeline Jenkins runs, rejected at admission.
// The pre-push verifier caught it before it shipped, and this case is what stops it coming
// back: the refusal arms in `prove-section-refusals.sh` all still pass against a build that
// over-refuses, because over-refusal is what they were written to allow.
//
// THE LOWERING IS GONE. `switch` is a real AST node (`SSwitch`) whose arms keep their
// source order, the interpreter catches `BreakSignal` AT THE SWITCH, and the admission walk
// tracks `break` and `continue` legality separately because a switch legalises only the
// first. Two intermediate positions — consume the arm-final `break`, then refuse the rest —
// are described in `Ast.fs` on the node itself, with what each one cost.
//
// `script-switch-break-and-fallthrough` is the wider case (a break inside an if inside an
// arm inside a LOOP, `default` chosen by position, and a genuine fallthrough). This one
// stays as the narrow regression pin for the plainest shape there is.
//
// THREE ARMS AND A DEFAULT, and the assertions are what each proves:
//   - `matched` takes the SECOND arm, so an engine that always ran the first passes
//     neither the output nor the workspace check.
//   - `after.txt` proves execution CONTINUED past the switch. An engine that let the
//     `break` escape as a signal — what this one did before FG-183 — ends the script
//     there, and the file is missing.
//   - the default arm is present but must NOT run, which an engine that mishandled a
//     non-matching subject would violate.
pipeline {
    agent any
    stages {
        stage('Switch') {
            steps {
                script {
                    def picked = 'none'

                    switch ('b') {
                        case 'a':
                            picked = 'first'
                            break
                        case 'b':
                            picked = 'second'
                            break
                        default:
                            picked = 'fell-through'
                    }

                    echo "picked:[${picked}]"
                    sh "printf 'picked=%s' '${picked}' > after.txt"
                }
            }
        }
    }
}
