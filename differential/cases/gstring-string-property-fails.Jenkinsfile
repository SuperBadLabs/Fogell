// FG-100. The sandbox rejects PROPERTY access on a String — `.length` is a field
// java.lang.String does not have, and the build FAILS:
//   groovy.lang.MissingPropertyException: No such field found: field java.lang.String length
// The METHOD form `.length()` is fine (`gstring-shared-binding`). Fogell rendered
// this chain as `null` and ran the step, because the fast path flattened
// `env.TARGET.length` into one environment lookup.
pipeline {
  agent any
  environment { TARGET = 'prod' }
  stages {
    stage('S') {
      steps {
        sh "echo prop:${env.TARGET.length}"
      }
    }
  }
}
