// FG-053(b). `skipStagesAfterUnstable()` reaches NESTED sequential stages, not
// just top-level ones.
//
// MEASURED on Jenkins 2.568.1: after `n1` marks the build unstable, BOTH the
// nested sibling `n2` AND the following top-level `after` are skipped, with the
// same sentence. Only `n1.txt` survives, so the workspace hash checks the skips
// happened rather than were announced.
//
// Enforcing the policy in the pipeline's own stage loop alone let `n2` run:
// nested stages go through `runStage` from inside WalkerOrchestration, which
// never saw the flag.
pipeline {
    agent any
    options { skipStagesAfterUnstable() }
    stages {
        stage('outer') {
            stages {
                stage('n1') {
                    steps {
                        sh 'echo n1 > n1.txt'
                        unstable('flaky')
                    }
                }
                stage('n2') { steps { sh 'echo n2 > n2.txt' } }
            }
        }
        stage('after') { steps { sh 'echo after > after.txt' } }
    }
}
