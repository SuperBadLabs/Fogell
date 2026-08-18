// FG-191. Displaying a SELF-REFERENTIAL map renders Groovy's own '(this Map)'
// instead of recursing. MEASURED before the guard: echo "${m}" on a cyclic map
// was a StackOverflow that KILLED THE PROCESS (exit 134) — no fault, no receipt,
// walker dead — where Jenkins prints [self:(this Map)] and succeeds.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def m = [:]
                    m.self = m
                    echo "show:${m}"
                    echo "after"
                }
            }
        }
    }
}
