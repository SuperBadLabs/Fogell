// FG-191. Comparing two DISTINCT self-referential maps fails on BOTH engines:
// Groovy's AbstractMap.equals chases the cycle into a JVM StackOverflowError and
// the build fails; Fogell detects the cycle pair and raises the matching fault.
// MEASURED before the fix: Fogell's chase killed the PROCESS instead — the
// difference between a red build and a dead walker.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def m = [:]
                    m.self = m
                    def n = [:]
                    n.self = n
                    echo "mapeq:${m == n}"
                    echo "after"
                }
            }
        }
    }
}
