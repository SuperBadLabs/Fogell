pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        sh 'touch pipeline-stage.txt'
      }
    }
  }
  post {
    always {
      sh 'touch pipeline-post.txt'
    }
  }
}

def rows = [[name: 'a'], [name: 'b']]
echo 'tail-before-spread'
rows*.name = 'x'
echo 'tail-after-spread'
