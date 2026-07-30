// FG-044. `withCredentials([usernamePassword(...)])` — 12 of the 23 corpus files that
// use credentials take this shape. Both variables must bind, both must be masked, and
// both must be unset after the block.
//
// The workspace records lengths and the username's masked state rather than values, so
// the receipt proves the binding without committing a secret. The USERNAME is not a
// secret on Jenkins and is not masked; the password is.
pipeline {
    agent any
    stages {
        stage('Bind') {
            steps {
                withCredentials([usernamePassword(credentialsId: 'fogell-userpass', usernameVariable: 'DEPLOY_USER', passwordVariable: 'DEPLOY_PASS')]) {
                    sh 'echo "user=$DEPLOY_USER" > user.txt'
                    sh 'echo "passlen=${#DEPLOY_PASS}" > passlen.txt'
                    sh 'env | grep -c "^DEPLOY_PASS=" > in_env.txt'
                }
                sh 'echo "after=[${DEPLOY_PASS:-unset}]" > after.txt'
            }
        }
    }
}
