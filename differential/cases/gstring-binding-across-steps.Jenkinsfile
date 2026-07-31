// FG-100. The script Binding is BUILD-scoped, not GString-scoped: an assignment
// made by a placeholder in one step is readable by a later step's GString. Jenkins
// prints its def-keyword advice once for the assignment — narration, excluded by
// prefix — and then `ok` from both steps.
pipeline {
  agent any
  environment { TARGET = 'prod' }
  stages {
    stage('S') {
      steps {
        echo "first:${x = 'ok'; x}"
        echo "second:$x"
      }
    }
  }
}
