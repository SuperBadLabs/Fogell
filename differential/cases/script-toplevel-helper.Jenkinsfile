// FG-188. A `def` helper declared OUTSIDE `pipeline { }` is in scope inside `script { }`.
//
// The parser located the `pipeline` block and discarded everything around it, so a
// top-level helper was invisible here and calling it failed as an unknown name. It is the
// corpus's commonest escape construct — 56 files declare one.
//
// TWO USES, because they fail differently. `greet` proves a plain call resolves and
// returns. `makeBody` returns a CLOSURE that is handed to `dir` as a value, which also
// exercises the captured environment travelling with it: `v` exists only in the helper's
// frame, so a body run against the call site's scope would not find it at all.
//
// That second half is why this case matters beyond its own ticket. The by-value capture
// fix had NO receipt until now — every spelling that exercised it needed either this or a
// closure-valued local to be invocable, and both were blocked.
def greet(name) {
    return "hello-" + name
}

def makeBody(v) {
    return { sh "printf item=%s '${v}' > captured.txt" }
}

pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    sh "printf greet=%s '${greet('world')}' > greet.txt"
                    dir('sub', makeBody('deep'))
                }
            }
        }
    }
}
