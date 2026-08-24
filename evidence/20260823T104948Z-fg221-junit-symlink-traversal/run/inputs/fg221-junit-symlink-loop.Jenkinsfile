// FG-221. Ant follows a self-referential directory symlink under its logical
// scanner path, then prunes the branch after the same canonical target has
// already been followed five times.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<testsuite name=\"looped\" time=\"1\"><testcase name=\"pass\"/></testsuite>' > reports/result.xml; ln -s . reports/loop"
                    def summary = junit(testResults: 'reports/**/*.xml')
                    echo "FG221_LOOP=SUMMARY=${summary.totalCount},${summary.failCount},${summary.skipCount},${summary.passCount};DURATION=${summary.duration}"
                    sh 'rm reports/loop; printf continued > continued.txt'
                }
            }
        }
    }
}
