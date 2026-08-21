pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        echo 'pipeline-body'
      }
    }
  }
}

def trailing(value) { echo "tail:${value}" }
trailing('ok')
