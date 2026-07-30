// FG-041b review fixes, both from PR #14. `PATH+TOOLS=` PREPENDS to PATH, and the
// PATH it builds on must be the EFFECTIVE last-wins one — a stage-level PATH must
// beat the pipeline-level one. Reading the first match instead produced
// `/tools:<pipeline-path>`, which can run the wrong executable.
//
// The evidence is which `tool` script actually runs: the stage PATH entry must be
// the one consulted after the prepended directory.
pipeline {
    agent any
    environment {
        PATH = "/pipeline-only:${PATH}"
    }
    stages {
        stage('Augment') {
            environment {
                PATH = "/stage-wins:${PATH}"
            }
            steps {
                withEnv(['PATH+TOOLS=/opt/tools/bin']) {
                    sh 'echo "$PATH" | tr ":" "\\n" | head -3 > head.txt'
                }
            }
        }
    }
}
