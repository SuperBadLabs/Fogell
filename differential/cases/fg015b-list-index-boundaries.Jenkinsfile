// FG-015b. Integer writes retain Groovy's negative-index, extension and
// evaluation-order boundaries. A too-negative plain assignment evaluates its
// RHS before the catchable fault; a compound update faults while reading the
// old value, before its RHS.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def events = []
                    def target = { xs -> events << 'receiver'; xs }
                    def key = { events << 'index'; 0 }
                    def rhs = { events << 'rhs'; 2 }
                    def xs = [1, 10]

                    target(xs)[key()] += rhs()
                    target(xs)[key()]++
                    target(xs)[key()]--
                    xs[-1] = 11
                    xs[4] = 5

                    def late = { events << 'plain-too-negative-rhs'; 'x' }
                    try {
                        xs[-6] = late()
                    } catch (ArrayIndexOutOfBoundsException ignored) {
                        events << 'plain-too-negative-caught'
                    }

                    try {
                        xs[-6] += (events << 'compound-too-negative-rhs')
                    } catch (ArrayIndexOutOfBoundsException ignored) {
                        events << 'compound-too-negative-caught'
                    }

                    def extensionRhs = { events << 'extension-compound-rhs'; 2 }
                    def extensionInt = [1]
                    try {
                        extensionInt[1] += extensionRhs()
                    } catch (NullPointerException ignored) {
                        events << 'extension-compound-caught'
                    }

                    def extensionString = [1]
                    extensionString[1] += 'x'

                    def extensionPostfix = []
                    try {
                        extensionPostfix[0]++
                    } catch (NullPointerException ignored) {
                        events << 'extension-postfix-caught'
                    }

                    echo "values:${xs}"
                    echo "extension:${extensionInt}:${extensionString}:${extensionPostfix}"
                    echo "order:${events}"
                }
            }
        }
    }
}
