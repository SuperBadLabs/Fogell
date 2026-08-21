pipeline {
  agent any
  stages {
    stage('retry-child-halt') {
      steps {
        script {
          retry(2) {
            dir('nested') {
              timeout(time: 30, unit: 'SECONDS') {
                withEnv(['INSIDE=yes']) {
                  def first = sh(
                    script: 'if [ -f ../attempted.txt ]; then exit 0; else touch ../attempted.txt; exit 1; fi',
                    returnStatus: true
                  )
                  if (first != 0) {
                    unstash 'missing'
                    sh 'printf wrong > wrong.txt'
                  }
                  sh 'printf "$INSIDE" > recovered.txt'
                }
              }
            }
          }
          sh 'printf continued > continued.txt'
        }
      }
    }
  }
}
