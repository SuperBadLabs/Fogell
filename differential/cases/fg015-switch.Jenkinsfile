// FG-015 closure audit: a matching switch arm assigns and break leaves the switch.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def selected = 'miss'
                    switch ('b') {
                        case 'a':
                            selected = 'a'
                            break
                        case 'b':
                            selected = 'b'
                            break
                        default:
                            selected = 'default'
                            break
                    }
                    sh "printf '%s' '${selected}' > switch.txt"
                }
            }
        }
    }
}
