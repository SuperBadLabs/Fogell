// FG-015: directly measured Jenkins 2.568.1 spread-dot semantic boundary.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def present = [[name: 'a'], [name: 'b']]*.name
                    def withNullElement = [[name: 'a'], null, [name: 'b']]*.name
                    def missingMapValues = [[name: 'a'], [:], [name: null]]*.name

                    def maybe = null
                    def nullReceiver = maybe*.name

                    def groups = [[child: [name: 'a']], [child: null], [child: [name: 'b']]]
                    def nested = groups*.child*.name
                    def safeAfterSpread = groups*.child?.name

                    def mapValue = [left: 1, right: 2]
                    def mapResult = mapValue*.key

                    def scalarResult = 'not-caught'
                    try {
                        def scalarProjection = [[name: 'a'], 42]*.name
                    } catch (MissingPropertyException e) {
                        scalarResult = 'caught'
                    }

                    def stringResult = 'not-caught'
                    try {
                        def stringProjection = 'ab'*.length
                    } catch (MissingPropertyException e) {
                        stringResult = 'caught'
                    }

                    sh "printf '%s' '${present}|${withNullElement}|${missingMapValues}|${nullReceiver}|${nested}|${safeAfterSpread}|${mapResult}|${scalarResult}|${stringResult}' > spread-dot-boundary.txt"
                }
            }
        }
    }
}
