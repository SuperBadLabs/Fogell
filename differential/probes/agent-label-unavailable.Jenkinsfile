pipeline {
  agent none

  stages {
    stage('unavailable') {
      agent { label 'fg253-not-offered' }
      steps {
        sh 'printf "must-not-run\n" > unavailable-agent-ran.txt'
      }
    }
  }
}
