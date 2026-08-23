// FG-211. A reached arbitrary document root owns its direct testcase when the
// testcase carries an explicit classname.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg211-junit-wrapper-classname-pass.txt; mkdir -p reports; printf '%s' '<wrapper><testcase classname=\"pkg.C\" name=\"case\"/></wrapper>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def passes = summary.passCount
                    if (summary.totalCount == 1 && summary.failCount == 0 && summary.skipCount == 0 && passes == 1 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 1,0,0,1,Integer > fg211-junit-wrapper-classname-pass.txt'
                    } else {
                        sh 'printf wrong > fg211-junit-wrapper-classname-pass.txt'
                    }
                }
            }
        }
    }
}
