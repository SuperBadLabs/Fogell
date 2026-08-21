pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def classify = { label, action ->
                        try {
                            def value = action()
                            echo "${label}:returned:${value}"
                        } catch (Exception e) {
                            echo "${label}:Exception:${e.class.name}"
                        } catch (Error e) {
                            echo "${label}:Error:${e.class.name}"
                        } catch (Throwable e) {
                            echo "${label}:Throwable:${e.class.name}"
                        }
                    }

                    def selfList = [null]
                    selfList[0] = selfList
                    classify('self-list-nested-display') { [selfList].toString() }
                    classify('self-list-echo-direct') {
                        echo selfList
                        'echo-returned'
                    }
                    classify('self-list-echo-named') {
                        echo message: selfList
                        'echo-returned'
                    }
                    classify('self-list-sh') {
                        sh script: selfList
                        'sh-returned'
                    }

                    writeFile file: 'payload.txt', text: 'payload'
                    classify('self-list-stash') {
                        stash name: selfList, includes: 'payload.txt'
                        'stash-returned'
                    }

                    def selfMap = [self: null]
                    selfMap.self = selfMap
                    classify('self-map-toString') { selfMap.toString() }
                    classify('self-map-interpolation') { "${selfMap}" }
                    classify('self-map-nested-display') { [selfMap].toString() }
                    classify('self-map-equality-same') { selfMap == selfMap }
                    classify('self-map-hashCode') { selfMap.hashCode() }
                    classify('self-map-echo') {
                        echo selfMap
                        'echo-returned'
                    }

                    def leftMap = [self: null]
                    def rightMap = [self: null]
                    leftMap.self = leftMap
                    rightMap.self = rightMap
                    classify('distinct-map-equality') { leftMap == rightMap }
                    classify('distinct-map-contains') { [leftMap].contains(rightMap) }
                    classify('distinct-map-unique') { [leftMap, rightMap].unique() }
                    classify('distinct-map-toSet') { [leftMap].toSet() }
                }
            }
        }
    }
}
