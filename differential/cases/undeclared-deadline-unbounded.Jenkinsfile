pipeline {
  agent any
  stages {
    stage('outlive-the-old-default') {
      steps {
        sh 'sleep 130 && echo survived > survived.txt'
      }
    }
  }
}
