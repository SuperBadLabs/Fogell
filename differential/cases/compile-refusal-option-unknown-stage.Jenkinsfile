// FG-133. A stage-level option name Jenkins does not know. The pinned reference compiler refuses the model — MEASURED on
// Jenkins 2.568.1 (2026-09-04): `Invalid option type "bogusOption". Valid option
// types: [...]`, the accepted set for this scope enumerated, nothing runs.
// Fogell refuses the same model before any effect and now says WHY, choosing
// among unknown-to-Jenkins, known-but-unimplemented and wrong-scope from the
// two measured sets. The receipt compares the typed refusal disposition,
// terminal result and workspace hash; compiler wording is outside the
// compatibility claim (FG-129).
pipeline {
    agent any

    stages {
        stage('must-not-run') {
            options { bogusOption() }
            steps { sh 'touch must-not-run.txt' }
        }
    }
}
