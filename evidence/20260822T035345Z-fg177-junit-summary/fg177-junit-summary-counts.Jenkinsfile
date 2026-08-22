// FG-177 slice 5. Jenkins' TestResultSummary is not a Map. Fogell models only
// the three integer count properties this case exercises; duration, getters,
// rendering, indexing, mutation, identity and reflection remain fail-closed.
pipeline {
    agent any
    stages {
        stage('summary') {
            steps {
                script {
                    sh "rm -rf reports fg177-junit-summary.txt; mkdir -p reports; printf '%s' '<testsuite name=\"summary\" tests=\"4\" failures=\"1\" errors=\"1\" skipped=\"1\"><testcase name=\"pass\"/><testcase name=\"fail\"><failure message=\"failed\"/></testcase><testcase name=\"error\"><error message=\"errored\"/></testcase><testcase name=\"skip\"><skipped/></testcase></testsuite>' > reports/summary.xml"
                    def summary = junit(testResults: 'reports/summary.xml')

                    if (summary.totalCount == 4 && summary.failCount == 2 && summary.skipCount == 1) {
                        sh 'printf 4,2,1 > fg177-junit-summary.txt'
                    } else {
                        sh 'printf wrong > fg177-junit-summary.txt'
                    }
                }
            }
        }
    }
}
