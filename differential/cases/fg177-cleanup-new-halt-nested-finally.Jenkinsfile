pipeline {
  agent any
  stages {
    stage('cleanup-new-halt-nested-finally') {
      steps {
        script {
          try {
            sh('original', 'extra')
          } finally {
            try {
              echo 'cleanup-before-new-halt'
              sh('cleanup', 'extra')
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
