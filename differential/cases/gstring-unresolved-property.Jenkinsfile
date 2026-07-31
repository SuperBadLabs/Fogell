// FG-100. A GString naming a variable bound NOWHERE is not empty text — it is a
// failed Groovy property lookup, and the build FAILS. Erasing it to "" would RUN a
// command the author never wrote: `deploy ${TARGET}` becoming `deploy `.
pipeline {
  agent any
  environment { TARGET = 'prod' }
  stages {
    stage('S') {
      steps {
        sh "echo bare:${MISSING_BARE_VAR}"
      }
    }
  }
}
