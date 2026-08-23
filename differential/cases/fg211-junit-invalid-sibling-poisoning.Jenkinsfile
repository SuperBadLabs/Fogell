// FG-211. A valid reached sibling cannot hide a later reached testcase whose
// identity cannot be resolved; the whole invocation terminates.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg211-junit-invalid-sibling-poisoning.txt; mkdir -p reports; printf '%s' '<wrapper><testsuite name=\"ValidOwner\"><testcase name=\"pass\"/></testsuite><testsuite><testcase name=\"case\"/></testsuite></wrapper>' > reports/result.xml"
                    junit(testResults: 'reports/result.xml')
                    sh 'printf wrong > fg211-junit-invalid-sibling-poisoning.txt'
                }
            }
        }
    }
}
