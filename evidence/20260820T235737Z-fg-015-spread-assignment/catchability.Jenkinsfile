class FG015CatchBox implements Serializable {
    String name
}

pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def results = []

                    def listRows = [[name: 'a'], [name: 'b']]
                    try {
                        listRows*.name = 'x'
                        results << "list:assigned:${listRows*.name}"
                    } catch (Throwable e) {
                        results << "list:${e.toString()}:${listRows*.name}"
                    }

                    def boxes = [new FG015CatchBox(name: 'a'), new FG015CatchBox(name: 'b')]
                    try {
                        boxes*.name = 'x'
                        results << "object:assigned:${boxes*.name}"
                    } catch (Throwable e) {
                        results << "object:${e.toString()}:${boxes*.name}"
                    }

                    def nullRows = [[name: 'a'], null, [name: 'b']]
                    try {
                        nullRows*.name = 'x'
                        results << "null-element:assigned:${nullRows*.name}"
                    } catch (Throwable e) {
                        results << "null-element:${e.toString()}:${nullRows*.name}"
                    }

                    def missingRows = [[name: 'a'], 42]
                    try {
                        missingRows*.name = 'x'
                        results << "missing:assigned:${missingRows[0].name}"
                    } catch (Throwable e) {
                        results << "missing:${e.toString()}:${missingRows[0].name}"
                    }

                    def absent = null
                    try {
                        absent*.name = 'x'
                        results << "null-receiver:assigned:${absent}"
                    } catch (Throwable e) {
                        results << "null-receiver:${e.toString()}:${absent}"
                    }

                    def nestedRows = [[child: [name: 'a']], [child: [name: 'b']]]
                    try {
                        nestedRows*.child*.name = 'x'
                        results << "nested:assigned:${nestedRows*.child*.name}"
                    } catch (Throwable e) {
                        results << "nested:${e.toString()}:${nestedRows*.child*.name}"
                    }

                    def safeRows = [[child: [name: 'a']], [child: null], [child: [name: 'b']]]
                    try {
                        safeRows*.child?.name = 'x'
                        results << "safe:assigned:${safeRows*.child*.name}"
                    } catch (Throwable e) {
                        results << "safe:${e.toString()}:${safeRows*.child*.name}"
                    }

                    echo results.join('\n')
                    sh 'touch after-caught-assignment.txt'
                }
            }
        }
    }
}
