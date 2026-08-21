pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def capture = { label, action ->
                        try {
                            echo "${label}:ok:${action()}"
                        } catch (Throwable e) {
                            echo "${label}:caught:${e.class.name}"
                        }
                    }

                    capture('acyclic-sort') { [3, 1, 2].sort().toString() }
                    capture('self-single-sort') {
                        def a = [null]
                        a[0] = a
                        [a].sort()
                        'done'
                    }
                    capture('self-alias-sort') {
                        def a = [null]
                        a[0] = a
                        [a, a].sort()
                        'done'
                    }
                    capture('distinct-self-sort') {
                        def a = [null]
                        def b = [null]
                        a[0] = a
                        b[0] = b
                        [a, b].sort()
                        'unexpected'
                    }
                    capture('mixed-alias-sort') {
                        def a = [null]
                        def m = [back: a]
                        a[0] = m
                        [a, a].sort()
                        'done'
                    }
                    capture('mixed-distinct-sort') {
                        def a = [null]
                        def ma = [back: a]
                        a[0] = ma
                        def b = [null]
                        def mb = [back: b]
                        b[0] = mb
                        [a, b].sort()
                        'unexpected'
                    }
                    capture('nested-alias-sort') {
                        def a = [null]
                        a[0] = a
                        def left = [a]
                        def right = [a]
                        [left, right].sort()
                        'done'
                    }
                    capture('nested-distinct-sort') {
                        def a = [null]
                        def b = [null]
                        a[0] = a
                        b[0] = b
                        [[a], [b]].sort()
                        'unexpected'
                    }
                    capture('distinct-self-min') {
                        def a = [null]
                        def b = [null]
                        a[0] = a
                        b[0] = b
                        [a, b].min()
                        'unexpected'
                    }
                    capture('distinct-self-max') {
                        def a = [null]
                        def b = [null]
                        a[0] = a
                        b[0] = b
                        [a, b].max()
                        'unexpected'
                    }
                    capture('distinct-self-unique') {
                        def a = [null]
                        def b = [null]
                        a[0] = a
                        b[0] = b
                        [a, b].unique(false)
                        'done'
                    }
                }
            }
        }
    }
}
