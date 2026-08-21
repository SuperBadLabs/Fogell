def git(value) {
  sh 'printf helper-body > helper-body.txt'
  return value
}

pipeline {
  agent any
  stages {
    stage('halt') {
      steps {
        script {
          def unreachableArg = {
            sh 'printf arg > arg.txt'
            return 'ignored'
          }
          sh('invalid', 'extra')
          git(MISSING)
          git(unreachableArg())
          sh(script: MISSING)
          sh(script: unreachableArg())
          sh(script: 'printf warned > warned.txt', fogellProbeUnknown: true)
          echo(message: 'must-not-print', fogellProbeUnknown: true)
          sh 'printf effect > effect.txt'
        }
      }
    }
  }
}
