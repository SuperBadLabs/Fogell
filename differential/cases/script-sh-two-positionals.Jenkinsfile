// FG-174. A hosted step with TWO positional arguments is a signature error, and this
// case exists for the step that had NO signature arm at all.
//
// `hostedSignatureError` had one arm per wrapper and a silent pass for everything else,
// so `sh('echo ran > ran.txt', 'ignored')` ran the FIRST positional and dropped the
// second, while Jenkins rejects the call and leaves the workspace EMPTY. Measured.
//
// The fix is a DEFAULT DENY on arity rather than a thirteenth arm — Jenkins maps a single
// positional onto a step's sole required parameter, and none of the fourteen steps in the
// script vocabulary takes two. `sh` is the case here because it is the one every pipeline
// uses; the rule it exercises covers `echo`, `git`, `stash` and the rest at the same time.
//
// Both `ran.txt` and `after.txt` must be ABSENT: the first proves the call was refused,
// the second that the refusal STOPPED the block rather than being reported and stepped over.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    sh('echo ran > ran.txt', 'ignored')
                    sh 'echo after > after.txt'
                }
            }
        }
    }
}
