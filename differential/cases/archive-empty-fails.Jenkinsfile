pipeline {
  agent any
  stages {
    stage('Nothing') {
      steps {
        archiveArtifacts artifacts: 'does-not-exist-*.zip'
      }
    }
  }
}
