// FG-212. With no testcase name, testcase classname, or owner name, the pinned
// parser terminates before classification; allowEmptyResults cannot admit it.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports fg212-junit-missing-name-terminal.txt; mkdir -p reports; printf '%s' '<wrapper><testcase/></wrapper>' > reports/result.xml"
                    junit(testResults: 'reports/result.xml', allowEmptyResults: true)
                    sh 'printf wrong > fg212-junit-missing-name-terminal.txt'
                }
            }
        }
    }
}
