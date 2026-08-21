pipeline {
  agent any
  stages {
    stage('cleanup-new-halt') {
      steps {
        script {
          try {
            sh('original', 'extra')
          } finally {
            echo 'cleanup-start'
            sh('cleanup', 'extra')
            sh 'echo deploy > deploy.txt'
            echo 'deploy-must-not-run'
          }
          echo 'after-must-not-run'
        }
      }
    }
  }
  post { failure { echo 'terminal-failure' } }
}
