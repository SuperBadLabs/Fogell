// FG-177 zero control. The report has three actual cases and no passes: one
// failure, one error, and one skip. passCount remains an Integer at zero and
// the failed reports keep the build UNSTABLE.
pipeline {
    agent any
    stages {
        stage('zero pass count') {
            steps {
                script {
                    sh "rm -rf reports fg177-junit-pass-count-zero.txt; mkdir -p reports; printf '%s' '<testsuite name=\"zero-pass-count\" tests=\"3\" failures=\"1\" errors=\"1\" skipped=\"1\"><testcase name=\"fail\"><failure message=\"failed\"/></testcase><testcase name=\"error\"><error message=\"errored\"/></testcase><testcase name=\"skip\"><skipped/></testcase></testsuite>' > reports/pass-count-zero.xml"
                    def summary = junit(testResults: 'reports/pass-count-zero.xml')
                    def passes = summary.passCount

                    if (summary.totalCount == 3 && summary.failCount == 2 && summary.skipCount == 1 && passes == 0 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 3,2,1,0,Integer > fg177-junit-pass-count-zero.txt'
                    } else {
                        sh 'printf wrong > fg177-junit-pass-count-zero.txt'
                    }
                }
            }
        }
    }
}
