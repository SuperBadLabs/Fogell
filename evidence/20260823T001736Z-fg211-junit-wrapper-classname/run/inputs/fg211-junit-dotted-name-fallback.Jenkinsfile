// FG-211. A dotted testcase name supplies identity when classname and owner
// name are both absent, for the root owner and a reached testsuite owner.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg211-junit-dotted-name-fallback.txt; mkdir -p reports; printf '%s' '<wrapper><testcase name=\"pkg.Root.case\"/><testsuite><testcase name=\"pkg.Suite.case\"/></testsuite></wrapper>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def passes = summary.passCount
                    if (summary.totalCount == 2 && summary.failCount == 0 && summary.skipCount == 0 && passes == 2 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 2,0,0,2,Integer > fg211-junit-dotted-name-fallback.txt'
                    } else {
                        sh 'printf wrong > fg211-junit-dotted-name-fallback.txt'
                    }
                }
            }
        }
    }
}
