// FG-205. `sort` over maps. Jenkins' comparator falls back to hashCode() when a
// map sits on either side of a compared pair, so a cyclic map overflows there
// (a StackOverflowError: an Error, not an Exception) wherever it sits — even
// when the pair already differs before the cycle is reached — while the same
// object on both sides returns before the fallback. The same-class scalar
// shapes the builtin still orders are sealed beside them. The ACYCLIC map
// order Jenkins produces (Java hash order) is deliberately not here: Fogell
// refuses it by name, and the ticket carries the probe.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def events = []
                    events << "ints:${[3, 1, 2].sort()}"
                    events << "strings:${['b', 'a', null].sort()}"
                    events << "bools:${[true, false].sort()}"

                    def m = [k: 1]
                    m.self = m
                    [m, m].sort()
                    events << 'alias-ok'

                    def m1 = [k: 1]
                    m1.self = m1
                    def m2 = [k: 1]
                    m2.self = m2
                    try {
                        [m1, m2].sort()
                        events << 'distinct-unexpected'
                    } catch (Throwable ignored) {
                        events << 'distinct-caught'
                    }
                    try {
                        [m1, m2].sort()
                        events << 'error-unexpected'
                    } catch (Error ignored) {
                        events << 'error-caught'
                    }
                    try {
                        try {
                            [m1, m2].sort()
                            events << 'exception-unexpected'
                        } catch (Exception ignored) {
                            events << 'exception-overcaught'
                        }
                    } catch (Throwable ignored) {
                        events << 'exception-escaped'
                    }
                    try {
                        [[m], [m]].sort()
                        events << 'nested-unexpected'
                    } catch (Throwable ignored) {
                        events << 'nested-caught'
                    }
                    try {
                        [[1, m1], [2]].sort()
                        events << 'early-unexpected'
                    } catch (Throwable ignored) {
                        events << 'early-caught'
                    }
                    try {
                        [m1, 5].sort()
                        events << 'scalar-unexpected'
                    } catch (Throwable ignored) {
                        events << 'scalar-caught'
                    }
                    try {
                        [5, m1].sort()
                        events << 'scalar2-unexpected'
                    } catch (Throwable ignored) {
                        events << 'scalar2-caught'
                    }
                    try {
                        [[k: 1, self: null], m1].sort()
                        events << 'acyclic-vs-cyclic-unexpected'
                    } catch (Throwable ignored) {
                        events << 'acyclic-vs-cyclic-caught'
                    }
                    echo "order:${events}"
                }
            }
        }
    }
}
