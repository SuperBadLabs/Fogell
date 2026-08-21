pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def eachSource = [1, 2]
                    def eachResult = eachSource.each { }
                    eachResult[0] = 9
                    echo "each:source=${eachSource};result=${eachResult}"

                    def findAllSource = [1, 2, 3]
                    def findAllResult = findAllSource.findAll { it > 1 }
                    findAllResult[0] = 9
                    echo "findAll:source=${findAllSource};result=${findAllResult}"

                    def reverseSource = [1, 2, 3]
                    def reverseResult = reverseSource.reverse()
                    reverseResult[0] = 9
                    echo "reverse:source=${reverseSource};result=${reverseResult}"

                    def collectSource = [1, 2]
                    def collectResult = collectSource.collect { it * 10 }
                    collectResult[0] = 9
                    echo "collect:source=${collectSource};result=${collectResult}"

                    def scalarSource = [1, 2, 3]
                    echo "scalar:find=${scalarSource.find { it > 1 }};any=${scalarSource.any { it == 2 }};every=${scalarSource.every { it > 0 }};first=${scalarSource.first()};last=${scalarSource.last()}"
                }
            }
        }
    }
}
