pipeline {
  agent any
  stages {
    stage('Fault target') {
      steps {
        sh 'echo FG_LUIGI_FAULT_STARTED; sleep 90; echo FG_LUIGI_FAULT_COMPLETED'
      }
    }
  }
}
