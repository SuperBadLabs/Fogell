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

                    def r = 1..3
                    def alias = r
                    capture('display-equality') { "r=${r};listEq=${r == [1, 2, 3]};rangeEq=${r == (1..3)};aliasEq=${r == alias}" }
                    capture('reads') { "zero=${r[0]};negative=${r[-1]};positiveOob=${r[5]}" }
                    capture('negative-oob') { r[-4] }

                    def plainEvents = []
                    capture('plain-write') {
                        def rhs = { plainEvents << 'rhs'; 9 }
                        try { r[0] = rhs(); 'unexpected' } catch (Throwable e) { "caught=${e.class.name};events=${plainEvents};range=${r}" }
                    }

                    def compoundEvents = []
                    capture('compound-write') {
                        def rhs = { compoundEvents << 'rhs'; 2 }
                        try { r[0] += rhs(); 'unexpected' } catch (Throwable e) { "caught=${e.class.name};events=${compoundEvents};range=${r}" }
                    }

                    capture('postfix-write') {
                        try { r[0]++; 'unexpected' } catch (Throwable e) { "caught=${e.class.name};range=${r}" }
                    }

                    capture('alias-write') {
                        try { alias[-1] = 7; 'unexpected' } catch (Throwable e) { "caught=${e.class.name};range=${r};alias=${alias}" }
                    }

                    capture('iteration') {
                        def eachSeen = []
                        r.each { eachSeen << it }
                        def forSeen = []
                        for (v in r) { forSeen << v }
                        "each=${eachSeen};for=${forSeen}"
                    }

                    capture('reverse') {
                        def reversed = r.reverse()
                        reversed[0] = 9
                        "source=${r};result=${reversed}"
                    }

                    capture('sort-noarg') {
                        def sorted = r.sort()
                        sorted[0] = 9
                        "source=${r};result=${sorted}"
                    }

                    capture('sort-false') {
                        def sorted = r.sort(false)
                        sorted[0] = 9
                        "source=${r};result=${sorted}"
                    }

                    capture('collect') {
                        def collected = r.collect { it * 10 }
                        collected[0] = 9
                        "source=${r};result=${collected}"
                    }

                    capture('descending') {
                        def d = 3..1
                        def seen = []
                        for (v in d) { seen << v }
                        "display=${d};zero=${d[0]};negative=${d[-1]};seen=${seen};equal=${d == [3, 2, 1]}"
                    }
                }
            }
        }
    }
}
