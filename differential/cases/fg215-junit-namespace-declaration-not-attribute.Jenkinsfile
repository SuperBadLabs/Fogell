// FG-215. LINQ exposes xmlns declarations as attributes, but DOM4J does not.
// xmlns:name therefore cannot provide the reached owner's class fallback.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<n:testsuite xmlns:n=\"urn:fg215:n\" xmlns:name=\"urn:fg215:decoy\"><n:testcase n:name=\"plain\"/></n:testsuite>' > reports/result.xml"
                    junit(testResults: 'reports/result.xml')
                    sh 'printf wrong > continued.txt'
                }
            }
        }
    }
}
