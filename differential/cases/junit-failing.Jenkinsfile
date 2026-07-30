pipeline {
  agent any
  stages {
    stage('Test') {
      steps {
        sh 'mkdir -p reports'
        sh 'printf "%s" "<testsuite name=\\"s\\" tests=\\"2\\" failures=\\"1\\" errors=\\"0\\" skipped=\\"0\\"><testcase name=\\"ok\\"/><testcase name=\\"bad\\"><failure message=\\"boom\\"/></testcase></testsuite>" > reports/results.xml'
        junit 'reports/*.xml'
      }
    }
  }
}
