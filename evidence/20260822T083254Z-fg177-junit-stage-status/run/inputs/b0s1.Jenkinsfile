// FG-177: suppressing the stage mark also implies build-result suppression in
// pinned JUnit 1416. Both post scopes select success and the later stage runs.
pipeline {
    agent any
    options { skipStagesAfterUnstable() }
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports *.txt; mkdir -p reports; printf '%s' '<testsuite name=\"matrix\" tests=\"4\" failures=\"1\" errors=\"1\" skipped=\"1\"><testcase classname=\"matrix.Sample\" name=\"pass\"/><testcase classname=\"matrix.Sample\" name=\"fail\"><failure message=\"failed\"/></testcase><testcase classname=\"matrix.Sample\" name=\"error\"><error message=\"errored\"/></testcase><testcase classname=\"matrix.Sample\" name=\"skip\"><skipped/></testcase></testsuite>' > reports/summary.xml"
                    def got = junit(testResults: 'reports/summary.xml', skipMarkingBuildUnstable: false, skipMarkingStageUnstable: true)
                    if (got.totalCount == 4 && got.failCount == 2 && got.skipCount == 1) {
                        sh 'printf 4,2,1 > counts.txt'
                    }
                    sh 'printf successor > successor.txt'
                }
            }
            post {
                always { sh 'printf always > stage-always.txt' }
                success { sh 'printf success > stage-success.txt' }
                unstable { sh 'printf wrong > wrong-stage.txt' }
            }
        }
        stage('later') {
            steps { sh 'printf later > later.txt' }
        }
    }
    post {
        success { sh 'printf success > pipeline-success.txt' }
        unstable { sh 'printf wrong > wrong-pipeline.txt' }
    }
}
