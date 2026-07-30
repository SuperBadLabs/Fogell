// FG-041b review fix, PR #14 round 11. With SEVERAL plain PATH assignments in one
// withEnv list, the augmentation must build on the LAST one — the environment is
// last-wins. Picking the first produced `/tools:/first` and discarded the effective
// `/second`. Only the first two entries are compared: the tail legitimately differs
// between a Jenkins container and this host.
//
// No PIPE: Jenkins' `sh -x` traces each pipeline component and the continuation does not
// start with '+ ', which is the FG-002c gap — the sibling PATH case was rewritten for the
// same reason. The claim is about PATH ordering, not trace formatting.
pipeline {
    agent any
    stages {
        stage('Last wins') {
            steps {
                withEnv(['PATH=/first:/usr/bin:/bin', 'PATH=/second:/usr/bin:/bin', 'PATH+TOOLS=/opt/tools/bin']) {
                    sh 'printf "%s:%s\\n" "${PATH%%:*}" "$(x=${PATH#*:}; printf %s "${x%%:*}")" > head.txt'
                }
            }
        }
    }
}
