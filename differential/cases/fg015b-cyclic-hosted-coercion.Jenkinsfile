// PR #110 exact-head review closure. A direct-self marker is legal only after
// interpolation has produced an ordinary String. Passing any cyclic typed
// collection to a hosted step raises StackOverflowError before dispatch.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def direct = [null]
                    direct[0] = direct
                    echo "marker:${direct}"

                    try {
                        echo direct
                        echo 'direct-echo:unexpected'
                    } catch (Error ignored) {
                        echo 'direct-echo:caught'
                    }

                    try {
                        sh script: direct
                        echo 'direct-sh:unexpected'
                    } catch (Error ignored) {
                        echo 'direct-sh:caught'
                    }

                    def longer = [null]
                    longer[0] = [back: longer]

                    try {
                        echo message: longer
                        echo 'long-echo:unexpected'
                    } catch (Error ignored) {
                        echo 'long-echo:caught'
                    }

                    try {
                        echo "${longer}"
                        echo 'long-interpolation:unexpected'
                    } catch (Error ignored) {
                        echo 'long-interpolation:caught'
                    }
                }
            }
        }
    }
}
