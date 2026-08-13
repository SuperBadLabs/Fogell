// FG-179. A closure captures its enclosing scope BY REFERENCE, so a mutation inside a
// hosted wrapper body is visible after the body returns.
//
// THIS IS THE CASE THE REF-CELL CHANGE EXISTS FOR, and its previous behaviour is the exact
// shape ADR 0001 calls the worst: the build SUCCEEDED and wrote `marker=before`. Not a
// crash, not a refusal — a green build carrying a value Groovy would never produce, because
// `Env.Vars` mapped a name to a VALUE and assignment rebuilt the map functionally. The
// closure updated a map only it held, and the scope that created the variable kept the old
// one.
//
// A RESULT-ONLY COMPARISON PASSES THE DEFECT. Both engines report success; the divergence
// is the file's contents, which is why the marker is written to the WORKSPACE and lands in
// the sealed hash rather than only being echoed.
//
// `dir('sub')` is deliberate: the mutation happens inside a HOSTED wrapper body, which
// re-enters the interpreter through the host callback. That is the path where the value
// crossed a boundary and was silently copied, and it is the one of FG-179's ten findings
// that was a false SUCCESS rather than a false failure.
//
// THE ORDINARY CLOSURE PATH IS COVERED BY `script-closure-mutates-ordinary`, and this
// comment claimed for two commits that it was still broken. It was not: ref cells fixed
// both paths at once. What still failed was FG-187 — a postfix index continuing across a
// newline, so `def a = false` and a following `[1].each { … }` parsed as one expression —
// which is why that case spells its semicolons explicitly.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def marker = 'before'

                    dir('sub') {
                        marker = 'after'
                    }

                    echo "marker:[${marker}]"
                    sh "printf 'marker=%s' '${marker}' > marker.txt"
                }
            }
        }
    }
}
