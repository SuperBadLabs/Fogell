pipeline {
  agent any
  stages {
    stage('cleanup-catch-recovery') {
      steps {
        script {
          retry(2) {
            try {
              sh('original', 'extra')
            } finally {
              try {
                sh 'exit 7'
              } catch (Exception caught) {
                echo "cleanup-caught:${caught}"
              }
              echo 'cleanup-recovered'
            }
          }
          echo 'after-must-not-run'
        }
      }
    }
  }
  post { failure { echo 'terminal-failure' } }
}
