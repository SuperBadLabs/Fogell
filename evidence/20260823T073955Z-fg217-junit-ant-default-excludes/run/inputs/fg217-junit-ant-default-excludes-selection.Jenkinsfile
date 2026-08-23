// FG-217. JUnit's Ant FileSet keeps default excludes enabled. A failing report
// under .svn must remain inert beside the visible corpus-shaped report.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf module .svn continued.txt; mkdir -p module .svn; printf '%s' '<testsuite name=\"visible\" time=\"1.25\"><testcase name=\"pass\"/></testsuite>' > module/test-results.xml; printf '%s' '<testsuite name=\"excluded\" time=\"2.5\"><testcase name=\"fail\"><failure/></testcase></testsuite>' > .svn/test-results.xml"
                    def summary = junit(testResults: '**/test-results.xml')
                    echo "FG217_DEFAULT_EXCLUDES=selection;SUMMARY=${summary.totalCount},${summary.failCount},${summary.skipCount},${summary.passCount};DURATION=${summary.duration}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
