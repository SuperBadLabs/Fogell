// FG-046 review fix, PR #17 round 5. An escaped dollar in a double-quoted prompt is
// LITERAL on Jenkins: `input message: "Deploy \$TARGET?"` shows `$TARGET`, not the value.
//
// Round 4 made the plain (sentinel-free) value the default for every step argument, so a
// NUL could never reach a shell. That was right, but `input` interpolates, so it needs the
// escape-preserving form — now carried alongside as `InterpolationSource` rather than
// replacing the safe default.
pipeline {
    agent any
    environment {
        TARGET = 'production'
    }
    stages {
        stage('Gate') {
            steps {
                timeout(time: 4, unit: 'SECONDS') {
                    input message: "Deploy \$TARGET to ${TARGET}?"
                }
            }
        }
    }
}
