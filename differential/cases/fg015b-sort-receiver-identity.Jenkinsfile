// PR #110 exact-head review closure. The no-copy sort overload mutates and
// returns one receiver identity; a cyclic comparison faults before replacement.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def xs = [2, 1, 2]
                    def alias = xs
                    def sorted = xs.sort()
                    sorted[0] = 9
                    echo "identity:${xs}:${alias}:${sorted}"

                    def left = [null]
                    def right = [null]
                    left[0] = left
                    right[0] = right
                    def cyclic = [left, right, 1]
                    try {
                        cyclic.sort()
                        echo 'cycle:unexpected'
                    } catch (Throwable ignored) {
                        echo "cycle:${cyclic[0] == left}:${cyclic[1] == right}:${cyclic[2] == 1}"
                    }
                }
            }
        }
    }
}
