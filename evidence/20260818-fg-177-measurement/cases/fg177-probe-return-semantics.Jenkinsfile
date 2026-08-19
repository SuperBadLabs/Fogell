// FG-177 measurement only: actual return class/value for every non-checkout
// script-step vocabulary entry. Map returns print the complete sorted key set.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh 'rm -rf fg177-return-*; printf seed > fg177-return-seed.txt; mkdir -p reports; printf "<testsuite tests=\"1\"><testcase name=\"ok\"/></testsuite>" > reports/fg177-return.xml'

                    def shValue = sh(script: 'true')
                    echo "FG177 RETURN sh CLASS=${shValue == null ? 'null' : shValue.getClass().getName()} VALUE=${shValue}"

                    def echoValue = echo(message: 'fg177-return-echo')
                    echo "FG177 RETURN echo CLASS=${echoValue == null ? 'null' : echoValue.getClass().getName()} VALUE=${echoValue}"

                    def archiveValue = archiveArtifacts(artifacts: 'fg177-return-seed.txt')
                    echo "FG177 RETURN archiveArtifacts CLASS=${archiveValue == null ? 'null' : archiveValue.getClass().getName()} VALUE=${archiveValue}"

                    def junitValue = junit(testResults: 'reports/fg177-return.xml')
                    echo "FG177 RETURN junit CLASS=${junitValue == null ? 'null' : junitValue.getClass().getName()} VALUE=${junitValue}"

                    dir('fg177-return-delete') {
                        sh 'printf seed > seed.txt'
                        def deleteValue = deleteDir()
                        echo "FG177 RETURN deleteDir CLASS=${deleteValue == null ? 'null' : deleteValue.getClass().getName()} VALUE=${deleteValue}"
                    }

                    dir('fg177-return-git') {
                        def gitValue = git(url: @@FOGELL_SCM_URL@@, branch: 'main')
                        echo "FG177 RETURN git CLASS=${gitValue == null ? 'null' : gitValue.getClass().getName()} VALUE=${gitValue}"
                        if (gitValue instanceof Map) {
                            echo "FG177 RETURN git KEYS=${gitValue.keySet().sort().join(',')}"
                            gitValue.keySet().sort().each { k -> echo "FG177 RETURN git ENTRY ${k} CLASS=${gitValue[k] == null ? 'null' : gitValue[k].getClass().getName()} VALUE=${gitValue[k]}" }
                        }
                    }

                    def stashValue = stash(name: 'fg177-return-stash', includes: 'fg177-return-seed.txt')
                    echo "FG177 RETURN stash CLASS=${stashValue == null ? 'null' : stashValue.getClass().getName()} VALUE=${stashValue}"

                    def unstableValue = unstable(message: 'fg177-return-unstable')
                    echo "FG177 RETURN unstable CLASS=${unstableValue == null ? 'null' : unstableValue.getClass().getName()} VALUE=${unstableValue}"

                    dir('fg177-return-unstash') {
                        def unstashValue = unstash(name: 'fg177-return-stash')
                        echo "FG177 RETURN unstash CLASS=${unstashValue == null ? 'null' : unstashValue.getClass().getName()} VALUE=${unstashValue}"
                    }

                    def dirValue = dir('fg177-return-dir') { return 'DIR-BODY' }
                    echo "FG177 RETURN dir CLASS=${dirValue == null ? 'null' : dirValue.getClass().getName()} VALUE=${dirValue}"

                    def timeoutValue = timeout(time: 1, unit: 'MINUTES') { return 'TIMEOUT-BODY' }
                    echo "FG177 RETURN timeout CLASS=${timeoutValue == null ? 'null' : timeoutValue.getClass().getName()} VALUE=${timeoutValue}"

                    def retryValue = retry(count: 1) { return 'RETRY-BODY' }
                    echo "FG177 RETURN retry CLASS=${retryValue == null ? 'null' : retryValue.getClass().getName()} VALUE=${retryValue}"

                    def withEnvValue = withEnv(['FG177_RETURN=value']) { return 'WITHENV-BODY' }
                    echo "FG177 RETURN withEnv CLASS=${withEnvValue == null ? 'null' : withEnvValue.getClass().getName()} VALUE=${withEnvValue}"

                    sh 'printf done > fg177-return-complete.txt'
                }
            }
        }
    }
}
