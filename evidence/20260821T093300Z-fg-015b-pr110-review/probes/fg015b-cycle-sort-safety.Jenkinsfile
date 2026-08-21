// PR #110 review fix. A script-created list cycle may reach sort(), but never
// the host runtime's structural comparer. Jenkins' exact alias/nesting and
// StackOverflowError catch boundaries are retained as load-bearing output.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def events = []
                    events << "acyclic:${[3, 1, 2].sort()}"

                    def alias = [null]
                    alias[0] = alias
                    [alias, alias].sort()
                    events << 'alias-ok'

                    def left = [null]
                    def right = [null]
                    left[0] = left
                    right[0] = right
                    try {
                        [left, right].sort()
                        events << 'distinct-unexpected'
                    } catch (Throwable ignored) {
                        events << 'distinct-caught'
                    }

                    def nestedCycle = [null]
                    nestedCycle[0] = nestedCycle
                    try {
                        [[nestedCycle], [nestedCycle]].sort()
                        events << 'nested-unexpected'
                    } catch (Error ignored) {
                        events << 'nested-caught'
                    }

                    def mixedLeft = [null]
                    def mixedLeftMap = [back: mixedLeft]
                    mixedLeft[0] = mixedLeftMap
                    def mixedRight = [null]
                    def mixedRightMap = [back: mixedRight]
                    mixedRight[0] = mixedRightMap
                    try {
                        [mixedLeft, mixedRight].sort()
                        events << 'mixed-unexpected'
                    } catch (Throwable ignored) {
                        events << 'mixed-caught'
                    }

                    try {
                        try {
                            [left, right].sort()
                            events << 'exception-unexpected'
                        } catch (Exception ignored) {
                            events << 'exception-overcaught'
                        }
                    } catch (Throwable ignored) {
                        events << 'exception-escaped'
                    }

                    echo "order:${events}"
                }
            }
        }
    }
}
