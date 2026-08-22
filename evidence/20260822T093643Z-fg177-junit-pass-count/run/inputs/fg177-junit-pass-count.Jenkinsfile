// FG-177. The pinned JUnit TestResultSummary exposes passCount as an Integer.
// Seven actual cases make the relation observable: four pass, one fails, one
// errors, and one is skipped. The failed reports keep the build UNSTABLE.
pipeline {
    agent any
    stages {
        stage('pass count') {
            steps {
                script {
                    sh "rm -rf reports fg177-junit-pass-count.txt; mkdir -p reports; printf '%s' '<testsuite name=\"pass-count\" tests=\"7\" failures=\"1\" errors=\"1\" skipped=\"1\"><testcase name=\"pass-a\"/><testcase name=\"pass-b\"/><testcase name=\"pass-c\"/><testcase name=\"pass-d\"/><testcase name=\"fail\"><failure message=\"failed\"/></testcase><testcase name=\"error\"><error message=\"errored\"/></testcase><testcase name=\"skip\"><skipped/></testcase></testsuite>' > reports/pass-count.xml"
                    def summary = junit(testResults: 'reports/pass-count.xml')
                    def passes = summary.passCount

                    if (summary.totalCount == 7 && summary.failCount == 2 && summary.skipCount == 1 && passes == 4 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 7,2,1,4,Integer > fg177-junit-pass-count.txt'
                    } else {
                        sh 'printf wrong > fg177-junit-pass-count.txt'
                    }
                }
            }
        }
    }
}
