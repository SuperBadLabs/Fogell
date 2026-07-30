// FG-041b review fix, PR #14 round 11. With SEVERAL plain PATH assignments in one
// withEnv list, the augmentation must build on the LAST one — the environment is
// last-wins. Picking the first produced `/tools:/first` and discarded the effective
// `/second`. Only the first two entries are compared: the tail legitimately differs
// between a Jenkins container and this host.
pipeline {
    agent any
    stages {
        stage('Last wins') {
            steps {
                withEnv(['PATH=/first:/usr/bin:/bin', 'PATH=/second:/usr/bin:/bin', 'PATH+TOOLS=/opt/tools/bin']) {
                    sh 'echo "$PATH" | cut -d: -f1,2 > head.txt'
                }
            }
        }
    }
}
