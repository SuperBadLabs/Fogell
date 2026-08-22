// FG-177. JUnit's build-level flag suppresses only the overall UNSTABLE result:
// failed reports are still parsed, the typed summary is still returned, the
// current stage continues, skipStagesAfterUnstable does not skip the next stage,
// and pipeline post selects success. Jenkins' node/stage warning decoration is
// outside the text/workspace differential contract and remains unmodelled.
pipeline {
    agent any
    options { skipStagesAfterUnstable() }
    stages {
        stage('summary') {
            steps {
                script {
                    sh "rm -rf reports fg177-junit-suppressed.txt later.txt post-success.txt wrong-post.txt; mkdir -p reports; printf '%s' '<testsuite name=\"summary\" tests=\"4\" failures=\"1\" errors=\"1\" skipped=\"1\"><testcase name=\"pass\"/><testcase name=\"fail\"><failure message=\"failed\"/></testcase><testcase name=\"error\"><error message=\"errored\"/></testcase><testcase name=\"skip\"><skipped/></testcase></testsuite>' > reports/summary.xml"
                    def summary = junit(testResults: 'reports/summary.xml', skipMarkingBuildUnstable: true)

                    if (summary.totalCount == 4 && summary.failCount == 2 && summary.skipCount == 1) {
                        sh 'printf 4,2,1 > fg177-junit-suppressed.txt'
                    } else {
                        sh 'printf wrong > fg177-junit-suppressed.txt'
                    }
                }
            }
        }
        stage('later') {
            steps { sh 'printf later > later.txt' }
        }
    }
    post {
        success { sh 'printf success > post-success.txt' }
        unstable { sh 'printf wrong > wrong-post.txt' }
    }
}
