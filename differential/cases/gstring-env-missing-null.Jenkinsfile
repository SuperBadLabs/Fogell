// FG-100. `${env.MISSING}` is NOT the failure the bare form is: `env` is a map, the
// read comes back null, and the null is stringified — the build passes and prints
// the four letters `null`. The same absent name behaves differently by SPELLING,
// which is exactly the sort of thing that must be measured rather than reasoned out.
pipeline {
  agent any
  environment { TARGET = 'prod' }
  stages {
    stage('S') {
      steps {
        sh "echo envpath:${env.MISSING_ENV_PATH}"
      }
    }
  }
}
