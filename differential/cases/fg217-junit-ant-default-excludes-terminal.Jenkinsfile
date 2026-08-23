// FG-217. Ant default excludes still apply when an include names the path
// literally. The excluded-only invocation must take the no-report terminal path.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf .svn continued.txt; mkdir -p .svn; printf '%s' '<testsuite name=\"excluded\" time=\"1.25\"><testcase name=\"pass\"/></testsuite>' > .svn/report.xml"
                    junit(testResults: '.svn/report.xml')
                    sh 'printf wrong > continued.txt'
                }
            }
        }
    }
}
