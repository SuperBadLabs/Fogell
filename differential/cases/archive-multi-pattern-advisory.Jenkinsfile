// FG-102. A comma-separated archive list: Jenkins validates the individual Ant
// masks and its advisory speaks about the FIRST unmatched one alone, while the
// Configuration-error line names the full list.
pipeline {
  agent any
  stages {
    stage('S') {
      steps {
        archiveArtifacts artifacts: 'missing/**,other-*.zip', allowEmptyArchive: true
      }
    }
  }
}
