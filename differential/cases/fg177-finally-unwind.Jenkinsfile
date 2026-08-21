def increment(value) {
  return value + 1
}

pipeline {
  agent any
  stages {
    stage('finally-unwind') {
      steps {
        script {
          def n = 0
          retry(2) {
            try {
              if (n == 0) {
                unstash 'missing-retry'
              }
            } finally {
              n = increment(n)
              echo "retry-cleanup:${n}"
            }
          }
          echo "retry-after:${n}"

          try {
            try {
              unstash 'missing-precedence'
            } finally {
              sh 'echo cleanup-fault > cleanup.txt; exit 7'
            }
          } catch (Exception caught) {
            echo 'cleanup-fault-caught'
          }
          echo 'cleanup-fault-after'

          def got = {
            try {
              unstash 'missing-return'
            } finally {
              return 7
            }
          }()
          echo "return-value:${got}"
          echo 'return-after'

          try {
            try {
              unstash 'missing-nested'
            } finally {
              echo 'inner-cleanup'
            }
          } finally {
            echo 'outer-cleanup'
          }
          echo 'must-not-run'
        }
      }
    }
  }
  post {
    failure {
      echo 'original-fault-preserved'
    }
  }
}
