pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<j:testsuite xmlns:j=\"urn:fg215:element\" xmlns:a=\"urn:fg215:attr\"><j:testcase name=\"simple\" a:classname=\"pkg.C\"/></j:testsuite>' > reports/result.xml"
                    def summary = null
                    catchError(buildResult: 'FAILURE', stageResult: 'FAILURE') { summary = junit(testResults: 'reports/result.xml') }
                    echo "FG215_ATTR=prefixed-case-classname;RETURN_NULL=${summary == null};STATUS=${currentBuild.currentResult}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}
