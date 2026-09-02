// FG-123. `ansiColor(<map>)` where the argument is an unquoted EXPRESSION, concatenating two literals.
//
// MEASURED on Jenkins 2.568.1 (2026-09-02, four transient probe jobs on the
// pinned lab): Jenkins EVALUATES the option's argument before setting TERM —
// `"${'xterm'}"` gives TERM=xterm, `"${env.JOB_NAME}"` gives the job name and
// `'xt' + 'erm'` gives xterm, all SUCCESS. Fogell copied the parser's
// UNEVALUATED source text into TERM and reported success — a green build with
// the wrong bytes, ADR 0001's worst outcome — so the argument now renders
// through the same strict GString/expression path as a step argument.
pipeline {
    agent any
    options {
        ansiColor('xt' + 'erm')
    }
    stages {
        stage('one') {
            steps {
                sh 'echo "TERM=[$TERM]" > term.txt; cat term.txt'
            }
        }
    }
}
