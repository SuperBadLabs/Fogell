pipeline {
  agent any
  stages {
    stage('Bounded output overflow') {
      steps {
        sh 'head -c 41943040 /dev/zero | tr "\\000" X'
      }
    }
  }
}
