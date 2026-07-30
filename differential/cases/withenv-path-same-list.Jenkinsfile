// FG-041b review fix, PR #14 round 3. When ONE withEnv supplies both a plain PATH
// override and an augmentation, the augmentation must build on the override from
// that same list. Taking the enclosing scope's PATH produced `/tools:<outer>` and
// silently discarded `/custom-base`.
//
// The override keeps the system directories: a PATH without /bin makes Jenkins'
// own durable-task wrapper unable to start, so the build never reaches a terminal
// state and the case becomes unrunnable rather than informative.
pipeline {
    agent any
    stages {
        stage('Same list') {
            steps {
                withEnv(['PATH=/custom-base:/usr/bin:/bin', 'PATH+TOOLS=/opt/tools/bin']) {
                    sh 'echo "$PATH" | cut -d: -f1,2 > head.txt'
                }
            }
        }
    }
}
