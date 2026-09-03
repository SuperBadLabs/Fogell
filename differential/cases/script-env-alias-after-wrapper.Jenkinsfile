// FG-203. `def snap = env` hands out an alias of the environment object; the
// ticket asked whether it behaves as a SNAPSHOT here where Jenkins hands out a
// live `EnvActionImpl`. MEASURED on Jenkins 2.568.1 (2026-09-03, a transient
// probe job on the pinned lab): an alias taken INSIDE `withEnv` reads the
// wrapper's value there and `null` after the wrapper exits, and an alias taken
// OUTSIDE reads the wrapper's value inside and `null` after — the alias follows
// the live environment on every read. Fogell reads the same four values.
pipeline {
    agent any
    stages {
        stage('one') {
            steps {
                script {
                    def snap = null
                    withEnv(['A=one']) {
                        snap = env
                        echo "inside=${snap.A}"
                    }
                    echo "after=${snap.A}"
                    echo "direct=${env.A}"
                    def snap2 = env
                    withEnv(['B=two']) {
                        echo "outer-alias-inside=${snap2.B}"
                    }
                    echo "outer-alias-after=${snap2.B}"
                    sh 'echo observed > observed.txt'
                }
            }
        }
    }
}
