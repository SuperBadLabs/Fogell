pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def capture = { label, action ->
                        try {
                            echo "${label}:${action()}"
                        } catch (Throwable e) {
                            echo "${label}:fault:${e.class.name}"
                        }
                    }

                    capture('sort-noarg') {
                        def xs = [2, 1, 2]
                        def alias = xs
                        def sorted = xs.sort()
                        sorted[0] = 9
                        "source=${xs};alias=${alias};result=${sorted}"
                    }

                    capture('sort-false') {
                        def xs = [2, 1, 2]
                        def alias = xs
                        def sorted = xs.sort(false)
                        sorted[0] = 9
                        "source=${xs};alias=${alias};result=${sorted}"
                    }

                    capture('sort-true') {
                        def xs = [2, 1, 2]
                        def alias = xs
                        def sorted = xs.sort(true)
                        sorted[0] = 9
                        "source=${xs};alias=${alias};result=${sorted}"
                    }

                    capture('sort-comparator') {
                        def xs = [2, 1, 2]
                        def sorted = xs.sort { a, b -> b <=> a }
                        sorted[0] = 9
                        "source=${xs};result=${sorted}"
                    }

                    capture('sort-key') {
                        def xs = [2, 1, 2]
                        def sorted = xs.sort { -it }
                        sorted[0] = 9
                        "source=${xs};result=${sorted}"
                    }

                    capture('reverse') {
                        def xs = [1, 2, 3]
                        def reversed = xs.reverse()
                        reversed[0] = 9
                        "source=${xs};result=${reversed}"
                    }

                    capture('collect') {
                        def xs = [1, 2, 3]
                        def collected = xs.collect { it * 10 }
                        collected[0] = 9
                        "source=${xs};result=${collected}"
                    }

                    capture('cycle-default-timing') {
                        def left = [null]
                        def right = [null]
                        left[0] = left
                        right[0] = right
                        def xs = [left, right, 1]
                        def caught = 'none'
                        try {
                            xs.sort()
                        } catch (Throwable e) {
                            caught = e.class.name
                        }
                        "caught=${caught};size=${xs.size()};firstLeft=${xs[0] == left};secondRight=${xs[1] == right};lastOne=${xs[2] == 1}"
                    }

                    capture('cycle-key-bypass') {
                        def left = [null]
                        def right = [null]
                        left[0] = left
                        right[0] = right
                        def xs = [left, right]
                        def sorted = xs.sort { 0 }
                        "same=${sorted == xs};size=${xs.size()}"
                    }
                }
            }
        }
    }
}
