// FG-183. A `switch` arm's `break` is ORDINARY GROOVY, and the pipeline must run.
//
// THIS CASE EXISTS BECAUSE THE FG-183 FIX NEARLY BROKE IT. Refusing a `break` with no
// enclosing loop is right for `dir('d') { break }`; the parser lowers `switch` to nested
// ifs, so a perfectly ordinary `case 'a': break` arrived at that check looking identical
// and would have been refused — a pipeline Jenkins runs, rejected at admission. The
// pre-push verifier caught it before it shipped, and this case is what stops it coming
// back: the refusal arms in `prove-section-refusals.sh` all still pass against a build
// that over-refuses, because over-refusal is what they were written to allow.
//
// The lowering now consumes the arm's trailing `break`, which is the statement's whole
// meaning in the nested-if form — the branches are already mutually exclusive.
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
