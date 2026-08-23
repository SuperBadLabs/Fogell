// FG-211. Direct classname-bearing cases owned by an arbitrary root retain the
// measured pass/failure/error/skipped classification.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg211-junit-wrapper-classname-markers.txt; mkdir -p reports; printf '%s' '<wrapper><testcase classname=\"pkg.Pass\" name=\"pass\"/><testcase classname=\"pkg.Fail\" name=\"fail\"><failure/></testcase><testcase classname=\"pkg.Error\" name=\"error\"><error/></testcase><testcase classname=\"\" name=\"skip\"><skipped/></testcase></wrapper>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def passes = summary.passCount
                    if (summary.totalCount == 4 && summary.failCount == 2 && summary.skipCount == 1 && passes == 1 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 4,2,1,1,Integer > fg211-junit-wrapper-classname-markers.txt'
                    } else {
                        sh 'printf wrong > fg211-junit-wrapper-classname-markers.txt'
                    }
                }
            }
        }
    }
}
