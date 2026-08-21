// PR #110 exact-head review closure. Jenkins CPS traversal re-reads unvisited
// indexes and its live bound: current values stay captured, visited writes do
// not rewrite history, and appends/null-extension become later iterations.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def eachXs = [1, 2]
                    def eachSeen = []
                    eachXs.each {
                        eachSeen << it
                        if (it == 1) {
                            eachXs[0] = 8
                            eachXs[1] = 9
                            eachXs << 3
                        }
                        if (it == 9) {
                            eachXs[0] = 7
                        }
                    }
                    echo "each:${eachSeen}:${eachXs}"

                    def collectXs = [1, 2]
                    def collected = collectXs.collect {
                        if (it == 1) {
                            collectXs[1] = 9
                            collectXs << 3
                        }
                        it * 10
                    }
                    echo "collect:${collected}:${collectXs}"

                    def filterXs = [1, 2]
                    def filtered = filterXs.findAll {
                        if (it == 1) {
                            filterXs[1] = 9
                            filterXs << 3
                        }
                        it > 1
                    }
                    echo "findAll:${filtered}:${filterXs}"

                    def forXs = [1, 2]
                    def forSeen = []
                    for (v in forXs) {
                        forSeen << v
                        if (v == 1) {
                            forXs[1] = 9
                            forXs[3] = 4
                        }
                    }
                    echo "for:${forSeen}:${forXs}"
                }
            }
        }
    }
}
