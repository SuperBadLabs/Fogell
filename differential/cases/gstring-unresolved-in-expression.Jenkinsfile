// FG-100. Strictness must survive INSIDE an expression. The bare-name fast path
// raises on `${MISSING}`, but `${MISSING + '-suffix'}` takes the interpreter path,
// where an unknown variable reads as null — so Fogell ran `echo expr:null-suffix`
// while Groovy's property lookup fails before the `+` is ever evaluated.
pipeline {
  agent any
  environment { TARGET = 'prod' }
  stages {
    stage('S') {
      steps {
        sh "echo expr:${MISSING_EXPR_VAR + '-suffix'}"
      }
    }
  }
}
