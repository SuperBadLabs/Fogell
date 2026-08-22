// FG-177. Hosted wrappers return their body's typed closure value.
// Explicit and implicit returns are distributed across all four wrappers;
// retry proves the final successful attempt supplies the result.
pipeline {
    agent any
    stages {
        stage('body-result') {
            steps {
                script {
                    def attempts = 0

                    def dirResult = dir('fg177-body-result-dir') {
                        return 42
                    }

                    def timeoutResult = timeout(time: 1, unit: 'MINUTES') {
                        ['timeout', 6]
                    }

                    def retryResult = retry(count: 2) {
                        attempts = attempts + 1
                        if (attempts == 1) {
                            sh 'exit 1'
                        }
                        [attempts, 'retry']
                    }

                    def withEnvResult = withEnv(['FG177_BODY_RESULT=inside']) {
                        return [env.FG177_BODY_RESULT, 8]
                    }

                    echo "dir:${dirResult + 1}"
                    echo "timeout:${timeoutResult[0]}:${timeoutResult[1] + 1}"
                    echo "retry:${retryResult[0] + 1}:${retryResult[1]}"
                    echo "withEnv:${withEnvResult[0]}:${withEnvResult[1] + 1}"
                }
            }
        }
    }
}
