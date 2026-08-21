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

def trailing(value = 'default') { echo "tail-default:${value}" }
trailing()
