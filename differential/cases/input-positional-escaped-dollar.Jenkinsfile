// FG-046 review fix, PR #17 round 6. The `#0` provenance entry for POSITIONAL arguments
// was described in the Step documentation and never produced, so a positional prompt fell
// back to the plain value and expanded an escaped dollar. Documented-but-absent is the
// same defect shape as a comment that over-claims what the code does.
pipeline {
    agent any
    environment {
        TARGET = 'production'
    }
    stages {
        stage('Gate') {
            steps {
                timeout(time: 4, unit: 'SECONDS') {
                    input "Deploy \$TARGET to ${TARGET}?"
                }
            }
        }
    }
}
