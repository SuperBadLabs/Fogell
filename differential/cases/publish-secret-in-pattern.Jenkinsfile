// FG-100. Rendering NAMED arguments means a credential can now reach a publishing
// argument, and `archiveArtifacts` puts the unmatched pattern into its own error
// message. That message is emitted by the walker, not by a shell — so masking the
// shell and echo paths could not catch it. This case exists to keep the secret out
// of the log on a path that never touches a process.
pipeline {
  agent any
  stages {
    stage('Leak') {
      steps {
        withCredentials([string(credentialsId: 'fogell-token', variable: 'TOKEN')]) {
          archiveArtifacts artifacts: "${TOKEN}/nothing-matches-this"
        }
      }
    }
  }
}
