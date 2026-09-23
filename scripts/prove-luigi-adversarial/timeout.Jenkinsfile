pipeline {
  agent any
  stages {
    stage('Bounded timeout') {
      steps {
        timeout(time: 1, unit: 'SECONDS') {
          sh 'echo FG_LUIGI_TIMEOUT; sleep 30'
        }
      }
    }
  }
}
