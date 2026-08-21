pipeline {
  agent any
  stages {
    stage('schema') {
      steps {
        script {
          try {
            dir() { sh 'printf wrong > wrong.txt' }
          } catch (NullPointerException expected) {
            sh 'printf dir > dir-caught.txt'
          }
          sh 'printf continued > continued.txt'
        }
      }
    }
  }
}
