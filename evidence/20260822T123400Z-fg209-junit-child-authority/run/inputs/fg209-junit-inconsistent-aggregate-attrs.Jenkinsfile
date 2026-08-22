// FG-209. Internally consistent testcase children outrank internally
// inconsistent but numeric suite aggregate attributes.
pipeline {
    agent any
    stages {
        stage('child-authority') {
            steps {
                script {
                    sh "rm -rf reports fg209-junit-inconsistent-aggregate-attrs.txt; mkdir -p reports; printf '%s' '<testsuite name=\"inconsistent\" tests=\"99\" failures=\"98\" errors=\"97\" skipped=\"96\"><testcase name=\"pass\"/><testcase name=\"fail\"><failure message=\"failed\"/></testcase><testcase name=\"error\"><error message=\"errored\"/></testcase><testcase name=\"skip\"><skipped/></testcase></testsuite>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def passes = summary.passCount
                    if (summary.totalCount == 4 && summary.failCount == 2 && summary.skipCount == 1 && passes == 1 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 4,2,1,1,Integer > fg209-junit-inconsistent-aggregate-attrs.txt'
                    } else {
                        sh 'printf wrong > fg209-junit-inconsistent-aggregate-attrs.txt'
                    }
                }
            }
        }
    }
}
