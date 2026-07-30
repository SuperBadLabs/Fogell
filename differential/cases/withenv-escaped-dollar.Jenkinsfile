// FG-041b review fix, PR #14 round 13. `withEnv` extracts its entries with a regex
// and never goes through the lexer, so the NUL-sentinel protection that keeps
// "\$NAME" literal inside an `environment` block did not apply here — the value was
// expanded where Groovy keeps it verbatim.
//
// The same review also asked for `env['NAME']` support. MEASURED: Jenkins' sandbox
// REJECTS that (`getAt` is not an approved signature) and the build fails, so
// supporting it would make Fogell run what Jenkins refuses. Not implemented, and the
// finding is answered with the receipt rather than the code.
pipeline {
    agent any
    environment {
        SEED = 'seed'
    }
    stages {
        stage('Compare') {
            steps {
                withEnv(["KEPT=hold-\$SEED", "LIVE=live-${SEED}"]) {
                    sh 'echo "$KEPT" > kept.txt'
                    sh 'echo "$LIVE" > live.txt'
                }
            }
        }
    }
}
