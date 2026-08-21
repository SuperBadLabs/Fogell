pipeline {
  agent any
  stages {
    stage('cleanup-status-retry') {
      steps {
        script {
          try {
            stash(name: 'original')
          } finally {
            retry(2) {
              archiveArtifacts(artifacts: 'missing/**')
            }
            sh 'echo deploy > deploy.txt'
          }
        }
      }
    }
  }
  post { failure { echo 'terminal-failure' } }
}
