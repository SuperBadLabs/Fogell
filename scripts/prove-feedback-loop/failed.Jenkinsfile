pipeline {
  agent any
  stages {
    stage('Verify candidate') {
      steps {
        sh 'printf "FG265_ASSERTION_FAILED expected=2 actual=1\n"; sleep 1; exit 1'
      }
    }
  }
}
