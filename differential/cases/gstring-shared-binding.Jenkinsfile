// FG-100. Placeholders in ONE GString share Groovy's script Binding: an assignment
// in the first is visible to the second. Jenkins also prints its "Did you forget
// the `def` keyword?" advice for the no-def assignment — narration, excluded by
// prefix (see Trace.isDiagnosticLine).
// The method-call row rides along: `.length()` is legal where the `.length`
// PROPERTY is not (`gstring-string-property-fails`).
pipeline {
  agent any
  environment { TARGET = 'prod' }
  stages {
    stage('S') {
      steps {
        echo "shared:${x = 'ok'; x}-${x}"
        sh "echo meth:${env.TARGET.length()}"
      }
    }
  }
}
