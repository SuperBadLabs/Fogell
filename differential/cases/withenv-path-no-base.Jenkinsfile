// FG-041b review fix, PR #14 round 6. `PATH+TOOLS=` with NO PATH declared anywhere
// used to produce `/tools:` — wiping the inherited PATH so ordinary tools in
// /usr/bin vanished. An earlier revision had the process-PATH fallback and a later
// edit of mine dropped it.
//
// Only the FIRST entry is compared: the rest of PATH legitimately differs between a
// Jenkins container and this host, so comparing all of it would measure the
// environment rather than the semantics. What must agree is that the augmentation
// is prepended AND that the inherited PATH survived enough for `cut` to be found.
pipeline {
    agent any
    stages {
        stage('No base PATH') {
            steps {
                withEnv(['PATH+TOOLS=/opt/tools/bin']) {
                    sh 'printf "%s\\n" "${PATH%%:*}" > first.txt'
                }
            }
        }
    }
}
