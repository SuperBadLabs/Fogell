pipeline {
  agent any
  stages {
    stage('Build') {
      steps {
        sh 'echo compiled > artifact.txt'
        sh 'cat artifact.txt'
      }
    }
  }
}
