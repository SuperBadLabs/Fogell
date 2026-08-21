pipeline {
  agent any
  stages {
    stage('halt') {
      steps {
        script {
          sh('invalid', 'extra')
          sh(script: 'printf warned > warned.txt', fogellProbeUnknown: true)
          echo(message: 'must-not-print', fogellProbeUnknown: true)
          sh 'printf effect > effect.txt'
        }
      }
    }
  }
}
