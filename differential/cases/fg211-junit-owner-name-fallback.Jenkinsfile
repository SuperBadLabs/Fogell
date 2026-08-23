// FG-211. A missing classname can use the reached owner's name for both the
// arbitrary document root and a direct testsuite edge.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg211-junit-owner-name-fallback.txt; mkdir -p reports; printf '%s' '<wrapper name=\"RootFallback\"><testcase name=\"root-case\"/><testsuite name=\"SuiteFallback\"><testcase name=\"suite-case\"/></testsuite></wrapper>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    def passes = summary.passCount
                    if (summary.totalCount == 2 && summary.failCount == 0 && summary.skipCount == 0 && passes == 2 && passes instanceof Integer && !(passes instanceof Long)) {
                        sh 'printf 2,0,0,2,Integer > fg211-junit-owner-name-fallback.txt'
                    } else {
                        sh 'printf wrong > fg211-junit-owner-name-fallback.txt'
                    }
                }
            }
        }
    }
}
