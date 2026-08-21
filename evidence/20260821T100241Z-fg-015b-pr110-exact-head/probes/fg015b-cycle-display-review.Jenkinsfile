pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def runCase = { label, action ->
                        try {
                            try {
                                action()
                                echo "${label}:unexpected-success"
                            } catch (Exception e) {
                                echo "${label}:caught-exception:${e.class.name}"
                            }
                        } catch (Throwable e) {
                            echo "${label}:escaped-exception:${e.class.name}"
                        }
                    }

                    def makeMixed = {
                        def xs = [null]
                        def m = [back: xs]
                        xs[0] = m
                        xs
                    }

                    runCase('println-mixed') { println(makeMixed()) }
                    runCase('interpolation-mixed') { echo "value=${makeMixed()}" }
                    runCase('tostring-mixed') { echo makeMixed().toString() }

                    def errorCycle = makeMixed()
                    try {
                        println(errorCycle)
                        echo 'error-catch:unexpected-success'
                    } catch (Error e) {
                        echo "error-catch:caught:${e.class.name}"
                    }

                    def throwableCycle = makeMixed()
                    try {
                        echo "value=${throwableCycle}"
                        echo 'throwable-catch:unexpected-success'
                    } catch (Throwable e) {
                        echo "throwable-catch:caught:${e.class.name}"
                    }

                    def directList = [null]
                    directList[0] = directList
                    echo "direct-list:${directList}"
                    def directMap = [:]
                    directMap.self = directMap
                    echo "direct-map:${directMap}"
                }
            }
        }
    }
}
