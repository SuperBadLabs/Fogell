pipeline {
  agent any
  stages {
    stage('Fail') {
      steps {
        sh 'echo before'
        sh 'exit 3'
      }
    }
  }
}
