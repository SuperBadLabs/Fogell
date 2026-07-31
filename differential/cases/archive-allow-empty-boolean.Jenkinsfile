// FG-100. An unquoted `true` is a Groovy LITERAL, not a name. Rendering named
// arguments classified `allowEmptyArchive: true` as an expression, and the
// interpolator's identifier fast path then resolved `true` as an environment
// variable — empty string — so an empty archive Jenkins permits was failed.
pipeline {
  agent any
  stages {
    stage('Archive') {
      steps {
        archiveArtifacts artifacts: 'no-such-dir/**', allowEmptyArchive: true
        echo 'still running after an empty archive'
      }
    }
  }
}
