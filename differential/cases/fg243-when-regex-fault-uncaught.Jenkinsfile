// FG-243 (the FG-241 shape its filing could not seal). A `when` expression
// whose regex does not compile throws PatternSyntaxException uncaught: both
// engines fail the build and skip the stage behind it for the earlier failure.
// Jenkins narrates the failure as a remoting-wrapped exception whose message
// spans three lines before the first frame; the comparator now reads that span
// as the head's (FG-243), excludes it with the frames, and counts it as the
// reported reason — the receipt this shape was blocked from until then.
pipeline {
    agent any
    stages {
        stage('before') {
            steps { echo 'before-ran' }
        }
        stage('gate') {
            when { expression { return 'ab' ==~ /a)b|ab/ } }
            steps { echo 'gate-ran' }
        }
        stage('after') {
            steps { echo 'after-ran' }
        }
    }
}
