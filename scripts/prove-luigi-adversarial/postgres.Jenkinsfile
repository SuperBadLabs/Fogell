pipeline {
  agent any
  stages {
    stage('Database outage target') {
      steps {
        sh 'echo FG_LUIGI_FAULT_STARTED; sleep 15; echo FG_LUIGI_FAULT_COMPLETED'
      }
    }
  }
}
