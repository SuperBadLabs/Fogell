// FG-100. A Groovy comment inside a placeholder is text, braces and all: the
// placeholder closes at the final brace and the expression evaluates around the
// comment. Also exercises source-order argument evaluation: `label:` binds x
// before `script:` reads it, as Groovy evaluates call arguments left to right.
pipeline {
  agent any
  environment { TARGET = 'prod' }
  stages {
    stage('S') {
      steps {
        sh "echo c:${1 /* } */ + 1}"
        sh label: "${x = 'ord'; x}", script: "echo order:$x"
      }
    }
  }
}
