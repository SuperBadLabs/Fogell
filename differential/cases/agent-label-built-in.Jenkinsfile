pipeline {
  agent { label 'built-in' }

  stages {
    stage('labelled') {
      steps {
        echo 'running on the built-in executor'
        sh 'printf "labelled-workspace\n" > agent-label.txt'
      }
    }
  }
}
