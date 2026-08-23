// FG-212. An explicit testcase classname makes a missing testcase name
// admissible; ordinary pass/failure/error/skipped classification still applies.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg212-junit-missing-name-classname-markers.txt; mkdir -p reports; printf '%s' '<wrapper><testcase classname=\"pkg.Pass\"/><testcase classname=\"pkg.Fail\"><failure/></testcase><testcase classname=\"pkg.Error\"><error/></testcase><testcase classname=\"\"><failure/><error/><skipped/></testcase></wrapper>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def passes = summary.passCount
                    if (summary.totalCount == 4 && summary.failCount == 2 && summary.skipCount == 1 && passes == 1 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 4,2,1,1,Integer > fg212-junit-missing-name-classname-markers.txt'
                    } else {
                        sh 'printf wrong > fg212-junit-missing-name-classname-markers.txt'
                    }
                }
            }
        }
    }
}
