pipeline {
  agent any
  stages {
    stage('Shell failure') {
      steps {
        sh 'printf "planted shell failure\n" >&2; exit 7'
      }
    }
  }
}
