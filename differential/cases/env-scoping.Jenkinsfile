pipeline {
  agent any
  environment {
    SCOPE = 'pipeline'
    SHARED = 'both'
  }
  stages {
    stage('Outer') {
      steps {
        sh 'echo scope=$SCOPE shared=$SHARED'
      }
    }
    stage('Override') {
      environment {
        SCOPE = 'stage'
      }
      steps {
        sh 'echo scope=$SCOPE shared=$SHARED'
      }
    }
  }
}
