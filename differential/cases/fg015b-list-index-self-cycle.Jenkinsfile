// FG-015b. A direct list self-reference uses Groovy's `(this Collection)`
// display marker and identity equality terminates. Longer cycles are retained
// in the measured evidence and unit-tested through a survivable fault because
// they are Jenkins-negative StackOverflowError cases, not tier-1 successes.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def xs = [null]
                    def alias = xs
                    xs[0] = xs
                    echo "self:${xs}"
                    echo "identity:${xs == alias}"
                }
            }
        }
    }
}
