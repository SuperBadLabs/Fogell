//// SCM JOB ////
// FG-260. An SCM-provided branch contains slashes, proving supplied ambient
// lookup and String.replace independently of the missing/sibling controls.
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
                echo "tag=${DOCKER_TAG} registry=${DOCKER_REGISTRY} branch=${GIT_BRANCH} id=${BUILD_ID}"
            }
        }
    }
}
