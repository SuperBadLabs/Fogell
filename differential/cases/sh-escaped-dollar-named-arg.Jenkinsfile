// FG-046 review fix, PR #17 round 4. Regression guard. Tracking quote kind for step
// arguments briefly wrote a NUL sentinel into every double-quoted named argument, and only
// `input` restores it — so `sh(script: "echo \$X")` handed the SHELL an embedded NUL.
// The sentinel now exists only where interpolation consumes it.
pipeline {
    agent any
    stages {
        stage('Shell') {
            steps {
                sh(script: "echo \$LITERAL_DOLLAR > out.txt")
                sh 'grep -c . out.txt > count.txt'
            }
        }
    }
}
