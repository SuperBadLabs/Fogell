// FG-100. A bare GString name resolves from the INHERITED agent environment, not
// only from what the pipeline declares — `${PATH}` succeeds on a declarative
// pipeline that never mentions it. The MissingPropertyException fires only for a
// name bound nowhere (`gstring-unresolved-property`). The value differs per agent,
// so the receipt compares a predicate on it rather than its content.
pipeline {
  agent any
  stages {
    stage('S') {
      steps {
        sh "echo inherited:${PATH != null}"
        sh "echo viaexpr:${PATH.length() > 0}"
      }
    }
  }
}
