// FG-179's TENTH shape, measurable only after FG-195: a closure PARAMETER named
// `env` shadows the Jenkins environment inside the closure — the parameter's cell is
// minted by Env.withVar and never enters JenkinsEnvCells, so the wrapper refresh
// neither touches it nor is touched by it — while `env.TARGET` outside the shadow
// still reads the wrapper's refresh. Both spellings previously failed here before
// they could be compared, which is why the FG-179 row carried it as unmeasured.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    withEnv(['TARGET=prod']) {
                        def f = { env -> "param:${env}" }
                        echo f('VALUE')
                        echo "global:${env.TARGET}"
                    }
                }
            }
        }
    }
}
