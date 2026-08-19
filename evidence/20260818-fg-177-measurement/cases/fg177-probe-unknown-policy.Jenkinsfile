// FG-177 measurement only: how Jenkins 2.568.1 treats an unknown named key on
// each step admitted by Fogell's script-step vocabulary. Each call is isolated
// by try/catch and writes a marker if control continues past the invocation.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh 'rm -rf fg177-unknown-*; printf seed > fg177-archive-seed.txt; mkdir -p reports; printf "<testsuite tests=\"1\"><testcase name=\"ok\"/></testsuite>" > reports/fg177.xml'

                    try {
                        sh(script: 'printf sh-ran > fg177-unknown-sh-ran.txt', fogellProbeUnknown: true)
                        sh 'printf after > fg177-unknown-sh-after.txt'
                        echo 'FG177 UNKNOWN sh CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN sh THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        echo(message: 'echo-ran', fogellProbeUnknown: true)
                        sh 'printf after > fg177-unknown-echo-after.txt'
                        echo 'FG177 UNKNOWN echo CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN echo THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        archiveArtifacts(artifacts: 'fg177-archive-seed.txt', fogellProbeUnknown: true)
                        sh 'printf after > fg177-unknown-archive-after.txt'
                        echo 'FG177 UNKNOWN archiveArtifacts CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN archiveArtifacts THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        junit(testResults: 'reports/fg177.xml', fogellProbeUnknown: true)
                        sh 'printf after > fg177-unknown-junit-after.txt'
                        echo 'FG177 UNKNOWN junit CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN junit THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        dir('fg177-unknown-delete') {
                            sh 'printf seed > seed.txt'
                            deleteDir(fogellProbeUnknown: true)
                            sh 'printf after > ../fg177-unknown-delete-after.txt'
                        }
                        echo 'FG177 UNKNOWN deleteDir CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN deleteDir THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        dir('fg177-unknown-git') {
                            git(url: @@FOGELL_SCM_URL@@, branch: 'main', fogellProbeUnknown: true)
                            sh 'printf after > ../fg177-unknown-git-after.txt'
                        }
                        echo 'FG177 UNKNOWN git CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN git THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        stash(name: 'fg177-unknown-stash', includes: 'fg177-archive-seed.txt', fogellProbeUnknown: true)
                        sh 'printf after > fg177-unknown-stash-after.txt'
                        echo 'FG177 UNKNOWN stash CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN stash THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        unstable(message: 'fg177-unknown-unstable', fogellProbeUnknown: true)
                        sh 'printf after > fg177-unknown-unstable-after.txt'
                        echo 'FG177 UNKNOWN unstable CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN unstable THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        unstash(name: 'fg177-unknown-stash', fogellProbeUnknown: true)
                        sh 'printf after > fg177-unknown-unstash-after.txt'
                        echo 'FG177 UNKNOWN unstash CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN unstash THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        dir(path: 'fg177-unknown-dir', fogellProbeUnknown: true) {
                            sh 'printf body > body.txt'
                        }
                        sh 'printf after > fg177-unknown-dir-after.txt'
                        echo 'FG177 UNKNOWN dir CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN dir THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        timeout(time: 1, unit: 'MINUTES', fogellProbeUnknown: true) {
                            sh 'printf body > fg177-unknown-timeout-body.txt'
                        }
                        sh 'printf after > fg177-unknown-timeout-after.txt'
                        echo 'FG177 UNKNOWN timeout CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN timeout THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        retry(count: 1, fogellProbeUnknown: true) {
                            sh 'printf body > fg177-unknown-retry-body.txt'
                        }
                        sh 'printf after > fg177-unknown-retry-after.txt'
                        echo 'FG177 UNKNOWN retry CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN retry THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        withEnv(overrides: ['FG177_UNKNOWN=value'], fogellProbeUnknown: true) {
                            sh 'printf %s "$FG177_UNKNOWN" > fg177-unknown-withenv-body.txt'
                        }
                        sh 'printf after > fg177-unknown-withenv-after.txt'
                        echo 'FG177 UNKNOWN withEnv CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN withEnv THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }
                }
            }
        }
    }
}
