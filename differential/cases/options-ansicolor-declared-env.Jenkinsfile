// FG-123. `ansiColor(<map>)` where the argument is an `env.` read of a variable DECLARED by the pipeline's own environment block.
//
// MEASURED on Jenkins 2.568.1 (2026-09-02, transient probe jobs on the pinned
// lab): the option's argument is evaluated BEFORE the `environment` block
// applies and before any variable it does not know exists — `env.<name>`
// renders `null` in both cases, SUCCESS — so TERM is the four letters
// `null`. Fogell renders the argument through the strict step-argument path
// over the Jenkins-provided values only, and `env.<unknown>` follows the
// measured step rule (receipt `gstring-env-missing-null`).
pipeline {
    agent any
    environment { MAPNAME = 'xterm' }
    options {
        ansiColor("${env.MAPNAME}")
    }
    stages {
        stage('one') {
            steps {
                sh 'echo "TERM=[$TERM]" > term.txt; cat term.txt'
            }
        }
    }
}
