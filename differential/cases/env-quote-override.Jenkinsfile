// FG-041b review fix, PR #14 round 5. A stage overriding a pipeline variable with a
// DIFFERENT quote form must be resolved with its OWN provenance. The first fix
// unioned the two scopes' literal-name sets, so a pipeline `'$SEED'` followed by a
// stage `"$SEED"` left the stage's GString literal. Codex warned about exactly this
// when reviewing the previous round and I took the shortcut anyway.
//
// Also covers the slashy form, which is a GString in Groovy and must interpolate.
pipeline {
    agent any
    environment {
        SEED = 'seed'
        SWAPPED = 'literal-${SEED}'
    }
    stages {
        stage('Override with a GString') {
            environment {
                SWAPPED = "expanded-${SEED}"
                SLASHY = /slashy-${SEED}/
            }
            steps {
                sh 'echo "$SWAPPED" > swapped.txt'
                sh 'echo "$SLASHY" > slashy.txt'
            }
        }
    }
}
