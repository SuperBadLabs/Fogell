// FG-178. `return` inside a hosted body returns from the CLOSURE, not from the script.
//
// MEASURED before the fix: Jenkins writes `after.txt`, Fogell's workspace was EMPTY.
// `ReturnSignal` propagated past the thunk and ended the whole script, so the following
// step was SILENTLY SKIPPED and the build still reported success — work not done, with
// nothing in the log to say so.
//
// The ordinary closure-call path already caught this signal; the hosted path did not,
// which is the entire defect. `after.txt` is the assertion: the terminal result is
// success either way, so only the workspace distinguishes the two engines.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    dir('sub') {
                        return
                    }
                    sh 'echo after > after.txt'
                }
            }
        }
    }
}
