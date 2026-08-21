// PR #110 review fix. Scalar index writes remain unsupported, but their read,
// RHS, operator and catch timing must match Jenkins exactly. Only the three RHS
// paths which Jenkins reaches may leave workspace evidence.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def events = []

                    try {
                        'ab'[0] += sh('printf plus > scalar-plus.txt')
                    } catch (SecurityException ignored) {
                        events << 'plus-caught'
                    }

                    try {
                        'ab'[9] += sh('printf wrong > scalar-oob.txt')
                    } catch (StringIndexOutOfBoundsException ignored) {
                        events << 'positive-oob-caught'
                    }

                    try {
                        'ab'[-3] += sh('printf wrong > scalar-negative-oob.txt')
                    } catch (ArrayIndexOutOfBoundsException ignored) {
                        events << 'negative-oob-caught'
                    }

                    try {
                        'ab'[-1] += sh('printf negative > scalar-negative.txt')
                    } catch (SecurityException ignored) {
                        events << 'negative-caught'
                    }

                    def integer = 7
                    try {
                        integer[0] += sh('printf wrong > scalar-integer-compound.txt')
                    } catch (SecurityException ignored) {
                        events << 'integer-compound-caught'
                    }

                    try {
                        integer[0] = sh('printf plain > scalar-integer-plain.txt')
                    } catch (SecurityException ignored) {
                        events << 'integer-plain-caught'
                    }

                    def nullValue = null
                    try {
                        nullValue[0] += sh('printf wrong > scalar-null.txt')
                    } catch (NullPointerException ignored) {
                        events << 'null-caught'
                    }

                    try {
                        'ab'[0]++
                    } catch (SecurityException ignored) {
                        events << 'postfix-caught'
                    }

                    echo "timing:${events}"
                }
            }
        }
    }
}
