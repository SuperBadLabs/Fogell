// FG-221. Ant's literal fast path excludes a dangling file symlink, while a
// wildcard scan retains the lexical entry and JUnit models it as an empty
// synthetic failure.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; ln -s missing.xml reports/broken.xml"
                    def literal = junit(testResults: 'reports/broken.xml', allowEmptyResults: true)
                    echo "FG221_DANGLING=literal:${literal.totalCount},${literal.failCount},${literal.skipCount},${literal.passCount};DURATION=${literal.duration}"
                    def wildcard = junit(testResults: 'reports/broken*.xml')
                    echo "FG221_DANGLING=wildcard:${wildcard.totalCount},${wildcard.failCount},${wildcard.skipCount},${wildcard.passCount};DURATION=${wildcard.duration}"
                    sh 'rm reports/broken.xml; printf continued > continued.txt'
                }
            }
        }
    }
}
