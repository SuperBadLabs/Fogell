// FG-260. Candidate-exact helper and environment declarations. A late overlay
// makes eager pipeline-scope evaluation observable: DOCKER_TAG remains null.1.
def getdockertag(){
    return "${env.GIT_BRANCH}".replace("/",".") + "."+"${env.BUILD_ID}"
}
pipeline {
    agent any
    environment {
        DOCKER_REGISTRY = "varunpalekar1/php-test"
        DOCKER_TAG = getdockertag()
    }
    stages {
        stage('probe') {
            steps {
                withEnv(['GIT_BRANCH=late/poison', 'BUILD_ID=99']) {
                    echo "tag=${DOCKER_TAG} registry=${DOCKER_REGISTRY} branch=${GIT_BRANCH} id=${BUILD_ID}"
                }
            }
        }
    }
}
