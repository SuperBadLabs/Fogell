// FG-179. The ORDINARY closure path — a builtin's trailing block — shares the enclosing
// scope exactly as a hosted wrapper body does.
//
// `script-closure-mutates-enclosing` covers the hosted path (`dir('sub') { … }`). This one
// covers `each`, which reaches the closure through `applyClosure` rather than through the
// host callback, and the two were reported as separate defects for months.
//
// WHY THE SEMICOLONS, and they are NOT stylistic: FG-187. Written with newlines alone,
// `def n = 0` followed by a line starting with `[` parses as ONE expression — `0[1,2]` —
// because the postfix index continues across the line break. The `each` then has a
// non-list receiver and the build fails with "method 'each' is not modelled". That defect
// is unrelated to closures and is filed separately; the semicolons keep this case
// measuring what it claims to measure. When FG-187 lands, drop them and the case should
// still pass — that is the check, not a tidy-up.
//
// THE COUNTER IS THE ASSERTION. `n` reaching 2 proves the closure wrote THROUGH the
// enclosing variable on BOTH iterations: a copy-per-call would leave 0, and a single
// shared write would leave 1. `marker` proves the same for a plain assignment. Both land
// in the workspace, because a result-only comparison passes an engine that computes the
// right value and hands the pipeline a stale one.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def n = 0
                    def marker = 'before'

                    [1, 2].each { n = n + 1; marker = 'after' }

                    echo "n:[${n}] marker:[${marker}]"
                    sh "printf 'n=%s marker=%s' '${n}' '${marker}' > closure.txt"
                }
            }
        }
    }
}
