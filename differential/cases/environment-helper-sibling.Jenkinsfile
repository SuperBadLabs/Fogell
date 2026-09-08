// FG-260. An unquoted expression uses the pre-block snapshot even though a
// quoted GString retains Declarative's left-to-right sibling resolver.
def getdockertag(){
    return "${env.GIT_BRANCH}".replace("/",".") + "."+"${env.BUILD_ID}"
}
pipeline {
    agent any
    environment {
        DOCKER_REGISTRY = "varunpalekar1/php-test"
        GIT_BRANCH = "sibling/poison"
        BUILD_ID = "99"
        DOCKER_TAG = getdockertag()
        COPY = "${GIT_BRANCH}"
    }
    stages {
        stage('probe') {
            steps {
                echo "tag=${DOCKER_TAG} registry=${DOCKER_REGISTRY} branch=${GIT_BRANCH} id=${BUILD_ID} copy=${COPY}"
            }
        }
    }
}
