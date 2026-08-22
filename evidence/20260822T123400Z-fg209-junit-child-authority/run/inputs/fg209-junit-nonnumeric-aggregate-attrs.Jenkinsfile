// FG-209. Nonnumeric suite aggregate attributes are inert metadata; named
// testcase children remain the count authority.
pipeline {
    agent any
    stages {
        stage('child-authority') {
            steps {
                script {
                    sh "rm -rf reports fg209-junit-nonnumeric-aggregate-attrs.txt; mkdir -p reports; printf '%s' '<testsuite name=\"nonnumeric\" tests=\"NaN\" failures=\"bogus\" errors=\"oops\" skipped=\"wat\"><testcase name=\"pass\"/><testcase name=\"fail\"><failure message=\"failed\"/></testcase><testcase name=\"error\"><error message=\"errored\"/></testcase><testcase name=\"skip\"><skipped/></testcase></testsuite>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def passes = summary.passCount
                    if (summary.totalCount == 4 && summary.failCount == 2 && summary.skipCount == 1 && passes == 1 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 4,2,1,1,Integer > fg209-junit-nonnumeric-aggregate-attrs.txt'
                    } else {
                        sh 'printf wrong > fg209-junit-nonnumeric-aggregate-attrs.txt'
                    }
                }
            }
        }
    }
}
