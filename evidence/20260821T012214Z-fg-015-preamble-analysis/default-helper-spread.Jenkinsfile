def helper(x = true) { x }
def rows = [[name: 'a'], [name: 'b']]
rows*.name = 'x'

pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        sh 'touch stage-ran.txt'
      }
    }
  }
  post {
    always {
      sh 'touch post-ran.txt'
    }
  }
}
