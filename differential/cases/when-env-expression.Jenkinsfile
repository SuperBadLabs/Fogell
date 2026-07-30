// FG-048 review fix. The NORMAL Jenkins predicate shape is `env.FOO`, not a bare
// `FOO`. Only bare names were bound, so `env.FOO` resolved to null, compared
// null to a string, and SKIPPED a stage Jenkins runs. Caught by a Codex review
// comment on PR #13. Both spellings must work, and a multi-statement closure
// must return its last expression.
pipeline {
    agent any
    environment {
        DEPLOY_ENV = 'prod'
    }
    stages {
        stage('env.X matches') {
            when {
                expression { return env.DEPLOY_ENV == 'prod' }
            }
            steps {
                sh 'echo env-dot-matched > envdot.txt'
            }
        }
        stage('env.X mismatches') {
            when {
                expression { return env.DEPLOY_ENV == 'staging' }
            }
            steps {
                sh 'echo SHOULD-NOT-RUN > never.txt'
            }
        }
        stage('multi-statement closure') {
            when {
                expression {
                    def wanted = 'prod'
                    env.DEPLOY_ENV == wanted
                }
            }
            steps {
                sh 'echo multi-statement-matched > multi.txt'
            }
        }
    }
}
