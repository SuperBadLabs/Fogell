// FG-179. A write made before a `break`, inside a NESTED block, survives the unwind.
//
// This was a STATED LIMIT on both `SSwitch` and `SWhile`: the nested block is its own
// `execBlock`, whose returned environment the unwind skips, so `if (x) { y = 1; break }`
// computed the value and threw it away. The note recording it predicted that both shapes
// would "dissolve when FG-179 makes variables ref cells". They did — a write now goes
// THROUGH the variable's cell, and no unwind can skip a mutation that already happened.
//
// BOTH SHAPES, because the limit was recorded for both and a case covering one would let
// the other regress silently. `y` comes from a switch arm, `z` from a while body.
//
// The `y = 9` after the `if` must NOT run: it is what distinguishes "the break left the
// arm and kept the write" from "the break was ignored and the arm ran on".
//
// Semicolons are FG-187, as in the sibling closure cases — a line starting with `[` or a
// statement following a complete expression can be swallowed by the previous line. Drop
// them when that lands; the case should still pass.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def y = 0
                    def z = 0

                    switch ('a') {
                        case 'a':
                            if (true) {
                                y = 1
                                break
                            }
                            y = 9
                    }

                    while (true) {
                        if (true) {
                            z = 1
                            break
                        }
                    }

                    echo "y:[${y}] z:[${z}]"
                    sh "printf 'y=%s z=%s' '${y}' '${z}' > unwind.txt"
                }
            }
        }
    }
}
