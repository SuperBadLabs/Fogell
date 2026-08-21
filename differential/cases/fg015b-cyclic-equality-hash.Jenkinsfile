// PR #110 exact-head review closure. Every recursive equality/hash walk keeps
// StackOverflowError ancestry; same-identity cycles and display-only direct-self
// markers retain Jenkins' non-recursive fast paths.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def left = [null]
                    def right = [null]
                    left[0] = left
                    right[0] = right
                    echo "identity:${left == left}:${left}"

                    try {
                        echo "equal:${left == right}"
                        echo 'equal:unexpected'
                    } catch (Error ignored) {
                        echo 'equal:caught'
                    }

                    try {
                        echo "contains:${[left].contains(right)}"
                        echo 'contains:unexpected'
                    } catch (Error ignored) {
                        echo 'contains:caught'
                    }

                    def leftMap = [self: null]
                    def rightMap = [self: null]
                    leftMap.self = leftMap
                    rightMap.self = rightMap

                    try {
                        echo "map-equal:${leftMap == rightMap}"
                        echo 'map-equal:unexpected'
                    } catch (Error ignored) {
                        echo 'map-equal:caught'
                    }

                    def keyed = [:]
                    try {
                        keyed[left] = 'x'
                        echo 'map-key:unexpected'
                    } catch (Error ignored) {
                        echo "map-key:caught:${keyed.size()}"
                    }
                }
            }
        }
    }
}
