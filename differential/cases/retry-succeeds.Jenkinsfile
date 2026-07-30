// FG-035. A body that fails once then succeeds consumes exactly two attempts
// and the build is SUCCESS — a retried failure is not a build failure.
pipeline {
    agent any
    stages {
        stage('Flaky') {
            steps {
                retry(3) {
                    sh 'echo attempt >> attempts.txt; if [ -f ok.txt ]; then exit 0; fi; touch ok.txt; exit 1'
                }
            }
        }
    }
}
