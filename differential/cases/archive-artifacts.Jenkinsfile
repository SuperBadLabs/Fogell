pipeline {
  agent any
  stages {
    stage('Produce') {
      steps {
        sh 'echo payload > out.txt'
        sh 'mkdir -p target && echo jar > target/app.jar'
        archiveArtifacts artifacts: 'out.txt, target/*.jar'
      }
    }
  }
}
