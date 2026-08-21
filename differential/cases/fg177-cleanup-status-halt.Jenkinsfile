pipeline {
  agent any
  stages {
    stage('cleanup-status-halt') {
      steps {
        script {
          try {
            stash(name: 'original')
          } finally {
            archiveArtifacts(artifacts: 'missing/**')
            sh 'echo deploy > deploy.txt'
          }
        }
      }
    }
  }
  post { failure { echo 'terminal-failure' } }
}
