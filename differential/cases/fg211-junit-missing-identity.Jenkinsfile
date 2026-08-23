// FG-211. A reached direct testcase with no classname, no dotted testcase name,
// and no owner name terminates the invocation; it is not a zero result.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg211-junit-missing-identity.txt; mkdir -p reports; printf '%s' '<wrapper><testcase name=\"case\"/></wrapper>' > reports/result.xml"
                    junit(testResults: 'reports/result.xml')
                    sh 'printf wrong > fg211-junit-missing-identity.txt'
                }
            }
        }
    }
}
