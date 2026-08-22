// FG-177 negative control. Presence is not truth: explicit false preserves the
// ordinary UNSTABLE result while the JUnit step still returns its typed counts
// and the rest of the current stage continues.
pipeline {
    agent any
    stages {
        stage('summary') {
            steps {
                script {
                    sh "rm -rf reports fg177-junit-explicit-false.txt post-unstable.txt wrong-post.txt; mkdir -p reports; printf '%s' '<testsuite name=\"summary\" tests=\"2\" failures=\"1\" errors=\"0\" skipped=\"0\"><testcase name=\"pass\"/><testcase name=\"fail\"><failure message=\"failed\"/></testcase></testsuite>' > reports/summary.xml"
                    def summary = junit(testResults: 'reports/summary.xml', skipMarkingBuildUnstable: false)

                    if (summary.totalCount == 2 && summary.failCount == 1 && summary.skipCount == 0) {
                        sh 'printf 2,1,0 > fg177-junit-explicit-false.txt'
                    } else {
                        sh 'printf wrong > fg177-junit-explicit-false.txt'
                    }
                }
            }
        }
    }
    post {
        unstable { sh 'printf unstable > post-unstable.txt' }
        success { sh 'printf wrong > wrong-post.txt' }
    }
}
