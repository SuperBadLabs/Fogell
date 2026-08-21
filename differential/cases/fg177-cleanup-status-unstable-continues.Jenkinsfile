pipeline {
  agent any
  stages {
    stage('cleanup-status-unstable-continues') {
      steps {
        script {
          try {
            stash(name: 'original')
          } finally {
            unstable(message: 'notice')
            echo 'cleanup-continues'
          }
        }
      }
    }
  }
  post { failure { echo 'terminal-failure' } }
}
