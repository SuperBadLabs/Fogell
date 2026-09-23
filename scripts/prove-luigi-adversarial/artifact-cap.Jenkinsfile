pipeline {
  agent any
  stages {
    stage('Artifact retention cap') {
      steps {
        sh 'head -c 2097152 /dev/zero > oversized.bin'
        archiveArtifacts artifacts: 'oversized.bin'
      }
    }
  }
}
