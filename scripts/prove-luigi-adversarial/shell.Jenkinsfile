pipeline {
  agent any
  stages {
    stage('Deliberate failure') {
      steps {
        sh 'echo FG_LUIGI_EXIT7 >&2; exit 7'
      }
    }
  }
}
