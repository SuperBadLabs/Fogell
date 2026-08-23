pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<testsuite name=\"suite\"><testcase classname=\"pkg.C\" name=\"pass\"/><testcase classname=\"pkg.C\" name=\"failure\"><failure message=\"boom\"/></testcase><testcase classname=\"pkg.C\" name=\"error\"><error message=\"kaboom\"/></testcase><testcase classname=\"pkg.C\" name=\"skip\"><skipped/></testcase></testsuite>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def pTotal = summary.totalCount
                    def pFail = summary.failCount
                    def pSkip = summary.skipCount
                    def pPass = summary.passCount
                    def gTotal = summary.getTotalCount()
                    def gFail = summary.getFailCount()
                    def gSkip = summary.getSkipCount()
                    def gPass = summary.getPassCount()
                    def allInteger = pTotal instanceof Integer && pFail instanceof Integer && pSkip instanceof Integer && pPass instanceof Integer && gTotal instanceof Integer && gFail instanceof Integer && gSkip instanceof Integer && gPass instanceof Integer
                    def anyLong = pTotal instanceof Long || pFail instanceof Long || pSkip instanceof Long || pPass instanceof Long || gTotal instanceof Long || gFail instanceof Long || gSkip instanceof Long || gPass instanceof Long
                    echo "FG213_COUNTS=properties:${pTotal},${pFail},${pSkip},${pPass};getters:${gTotal},${gFail},${gSkip},${gPass};allInteger=${allInteger};anyLong=${anyLong}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
