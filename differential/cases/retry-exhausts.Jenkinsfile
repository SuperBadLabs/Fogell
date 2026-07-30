// FG-035. `retry(3)` around a body that always fails runs it THREE times in
// total, not four, and with no delay between attempts. The attempt count is
// recorded in the workspace so the receipt proves N, not just the verdict.
pipeline {
    agent any
    stages {
        stage('Flaky') {
            steps {
                retry(3) {
                    sh 'echo attempt >> attempts.txt; exit 7'
                }
            }
        }
    }
}
