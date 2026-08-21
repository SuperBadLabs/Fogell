// PR #110 exact-head review closure. IntRange remains list-like for reads,
// equality and traversal, while every replacement reaches a typed immutable
// write fault after Jenkins' measured RHS/read/operator timing.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def r = 1..3
                    def alias = r
                    def eachSeen = []
                    r.each { eachSeen << it }
                    def forSeen = []
                    for (v in 3..1) { forSeen << v }
                    def reversed = r.reverse()
                    reversed[0] = 9
                    def collected = r.collect { it * 10 }
                    collected[0] = 8

                    echo "range:${r}:${r[0]}:${r[-1]}:${r[5]}:${r == [1, 2, 3]}"
                    echo "iteration:${eachSeen}:${forSeen}"
                    echo "fresh:${reversed}:${collected}:${r}"

                    def events = []
                    def rhs = { events << 'plain-rhs'; 9 }
                    def compoundRhs = { events << 'compound-rhs'; 2 }
                    try { r[0] = rhs() } catch (UnsupportedOperationException ignored) { events << 'plain-caught' }
                    try { r[0] += compoundRhs() } catch (UnsupportedOperationException ignored) { events << 'compound-caught' }
                    try { r[0]++ } catch (UnsupportedOperationException ignored) { events << 'postfix-caught' }
                    try { alias[-1] = 7 } catch (UnsupportedOperationException ignored) { events << 'alias-caught' }
                    try { r.sort() } catch (UnsupportedOperationException ignored) { events << 'sort-caught' }
                    try { r[-4] } catch (ArrayIndexOutOfBoundsException ignored) { events << 'negative-oob-caught' }

                    echo "writes:${events}:${r}:${alias}"
                }
            }
        }
    }
}
