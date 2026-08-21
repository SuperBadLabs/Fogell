pipeline {
  agent any
  stages {
    stage('cleanup-status-nested-finally') {
      steps {
        script {
          try {
            stash(name: 'original')
          } finally {
            try {
              archiveArtifacts(artifacts: 'missing/**')
              echo 'inner-successor-must-not-run'
            } finally {
              echo 'nested-finally-runs'
            }
            echo 'outer-successor-must-not-run'
          }
        }
      }
    }
  }
  post { failure { echo 'terminal-failure' } }
}
