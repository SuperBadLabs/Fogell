class FG015Box implements Serializable {
    String name
}

pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                sh 'touch before-assignment.txt'
                script {
                    def boxes = [new FG015Box(name: 'a'), new FG015Box(name: 'b')]
                    boxes*.name = 'x'
                    sh "printf '%s' '${boxes*.name}' > assignment-result.txt"
                }
                sh 'touch after-assignment.txt'
            }
        }
    }
}
