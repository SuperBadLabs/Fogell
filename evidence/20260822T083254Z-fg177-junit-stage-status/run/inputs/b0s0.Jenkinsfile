// FG-177 control: with both JUnit marking flags false, failing tests mark both
// the probe stage and overall build unstable. The current stage continues,
// skipStagesAfterUnstable skips the later stage, and both post scopes select
// their unstable arms.
pipeline {
    agent any
    options { skipStagesAfterUnstable() }
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports *.txt; mkdir -p reports; printf '%s' '<testsuite name=\"matrix\" tests=\"4\" failures=\"1\" errors=\"1\" skipped=\"1\"><testcase classname=\"matrix.Sample\" name=\"pass\"/><testcase classname=\"matrix.Sample\" name=\"fail\"><failure message=\"failed\"/></testcase><testcase classname=\"matrix.Sample\" name=\"error\"><error message=\"errored\"/></testcase><testcase classname=\"matrix.Sample\" name=\"skip\"><skipped/></testcase></testsuite>' > reports/summary.xml"
                    def got = junit(testResults: 'reports/summary.xml', skipMarkingBuildUnstable: false, skipMarkingStageUnstable: false)
                    if (got.totalCount == 4 && got.failCount == 2 && got.skipCount == 1) {
                        sh 'printf 4,2,1 > counts.txt'
                    }
                    sh 'printf successor > successor.txt'
                }
            }
            post {
                always { sh 'printf always > stage-always.txt' }
                unstable { sh 'printf unstable > stage-unstable.txt' }
                success { sh 'printf wrong > wrong-stage.txt' }
            }
        }
        stage('later') {
            steps { sh 'printf wrong > wrong-later.txt' }
        }
    }
    post {
        unstable { sh 'printf unstable > pipeline-unstable.txt' }
        success { sh 'printf wrong > wrong-pipeline.txt' }
    }
}
