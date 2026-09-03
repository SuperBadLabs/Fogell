// FG-123. `ansiColor(<map>)` where the argument's placeholder ASSIGNS a script
// binding and yields it: `"${x = 'xterm'; x}"`.
//
// MEASURED on Jenkins 2.568.1 (2026-09-02, a transient probe job on the pinned
// lab, raised by Codex on PR #336): the option's argument is evaluated in the
// script's own binding, so the assignment survives — a later `echo "x=${x}"`
// prints `x=xterm` — Jenkins prints the def-keyword advisory for the untyped
// field, and TERM is `xterm`; SUCCESS. The first cut rendered the option
// through a throwaway binding: TERM was right and the later read failed the
// build. The argument now renders through the run-scoped binding every step
// argument shares.
pipeline {
    agent any
    options {
        ansiColor("${x = 'xterm'; x}")
    }
    stages {
        stage('one') {
            steps {
                sh 'echo "TERM=[$TERM]" > term.txt; cat term.txt'
                echo "x=${x}"
            }
        }
    }
}
