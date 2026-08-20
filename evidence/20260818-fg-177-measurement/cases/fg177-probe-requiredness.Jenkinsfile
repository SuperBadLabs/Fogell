// FG-177 measurement only: whether the advertised primary parameter is actually
// required at runtime. Wrapper calls retain a body while omitting the parameter.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh 'rm -rf fg177-missing-*'

                    try { sh(); sh 'printf after > fg177-missing-sh-after.txt'; echo 'FG177 MISSING sh CONTINUED' }
                    catch (Exception e) { echo "FG177 MISSING sh THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try { echo(); sh 'printf after > fg177-missing-echo-after.txt'; echo 'FG177 MISSING echo CONTINUED' }
                    catch (Exception e) { echo "FG177 MISSING echo THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try { archiveArtifacts(); sh 'printf after > fg177-missing-archive-after.txt'; echo 'FG177 MISSING archiveArtifacts CONTINUED' }
                    catch (Exception e) { echo "FG177 MISSING archiveArtifacts THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try { junit(); sh 'printf after > fg177-missing-junit-after.txt'; echo 'FG177 MISSING junit CONTINUED' }
                    catch (Exception e) { echo "FG177 MISSING junit THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try {
                        dir('fg177-missing-delete') { sh 'printf seed > seed.txt'; deleteDir(); sh 'printf after > ../fg177-missing-delete-after.txt' }
                        echo 'FG177 MISSING deleteDir CONTINUED'
                    } catch (Exception e) { echo "FG177 MISSING deleteDir THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try { git(); sh 'printf after > fg177-missing-git-after.txt'; echo 'FG177 MISSING git CONTINUED' }
                    catch (Exception e) { echo "FG177 MISSING git THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try { stash(); sh 'printf after > fg177-missing-stash-after.txt'; echo 'FG177 MISSING stash CONTINUED' }
                    catch (Exception e) { echo "FG177 MISSING stash THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try { unstable(); sh 'printf after > fg177-missing-unstable-after.txt'; echo 'FG177 MISSING unstable CONTINUED' }
                    catch (Exception e) { echo "FG177 MISSING unstable THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try { unstash(); sh 'printf after > fg177-missing-unstash-after.txt'; echo 'FG177 MISSING unstash CONTINUED' }
                    catch (Exception e) { echo "FG177 MISSING unstash THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try {
                        dir() { sh 'printf body > fg177-missing-dir-body.txt' }
                        sh 'printf after > fg177-missing-dir-after.txt'
                        echo 'FG177 MISSING dir CONTINUED'
                    } catch (Exception e) { echo "FG177 MISSING dir THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try {
                        timeout() { sh 'printf body > fg177-missing-timeout-body.txt' }
                        sh 'printf after > fg177-missing-timeout-after.txt'
                        echo 'FG177 MISSING timeout CONTINUED'
                    } catch (Exception e) { echo "FG177 MISSING timeout THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try {
                        retry() { sh 'printf body > fg177-missing-retry-body.txt' }
                        sh 'printf after > fg177-missing-retry-after.txt'
                        echo 'FG177 MISSING retry CONTINUED'
                    } catch (Exception e) { echo "FG177 MISSING retry THREW ${e.getClass().getName()}: ${e.getMessage()}" }

                    try {
                        withEnv() { sh 'printf body > fg177-missing-withenv-body.txt' }
                        sh 'printf after > fg177-missing-withenv-after.txt'
                        echo 'FG177 MISSING withEnv CONTINUED'
                    } catch (Exception e) { echo "FG177 MISSING withEnv THREW ${e.getClass().getName()}: ${e.getMessage()}" }
                }
            }
        }
    }
}
