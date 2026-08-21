pipeline {
  agent any
  stages {
    stage('cleanup-new-halt-return') {
      steps {
        script {
          def got = {
            try {
              sh('original', 'extra')
            } finally {
              try {
                sh('cleanup', 'extra')
                return 7
              } finally {
                return 9
              }
            }
          }()
          echo "return-value:${got}"
          echo 'after-return'
        }
      }
    }
  }
}
