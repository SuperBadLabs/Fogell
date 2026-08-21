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

                    def direct = [null]
                    direct[0] = direct
                    classify('direct-toString') { direct.toString() }
                    classify('direct-interpolation') { "${direct}" }
                    classify('direct-equality-same') { direct == direct }

                    def left = [null]
                    def right = [null]
                    left[0] = left
                    right[0] = right
                    classify('distinct-list-equality') { left == right }
                    classify('distinct-list-inequality') { left != right }
                    classify('distinct-list-ordering') { left <=> right }
                    classify('contains-same') { [left].contains(left) }
                    classify('contains-distinct') { [left].contains(right) }
                    classify('hashCode-cycle') { left.hashCode() }
                    classify('toSet-cycle') { [left].toSet() }
                    classify('unique-cycle') { [left, right].unique() }

                    def listMap = [null]
                    def nestedMap = [back: listMap]
                    listMap[0] = nestedMap
                    classify('list-map-toString') { listMap.toString() }
                    classify('list-map-interpolation') { "${listMap}" }
                    classify('map-value-toString') { nestedMap.toString() }
                    classify('map-value-interpolation') { "${nestedMap}" }
                    classify('map-key-insert') {
                        def keyed = [:]
                        keyed[listMap] = 'x'
                        keyed.size()
                    }

                    classify('echo-direct-cycle') {
                        echo listMap
                        'echo-returned'
                    }
                    classify('echo-named-cycle') {
                        echo message: listMap
                        'echo-returned'
                    }
                    classify('sh-named-cycle') {
                        sh script: listMap
                        'sh-returned'
                    }

                    writeFile file: 'payload.txt', text: 'payload'
                    classify('stash-name-cycle') {
                        stash name: listMap, includes: 'payload.txt'
                        'stash-returned'
                    }
                }
            }
        }
    }
}
