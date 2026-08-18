// FG-176's other direction, pinned so the fix cannot overshoot: an UNCAUGHT
// shell failure still fails the build, and the step after it never runs. The
// catchable fault is a channel, not an amnesty.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    sh 'exit 1'
                    sh 'echo never > never.txt'
                }
            }
        }
    }
}
