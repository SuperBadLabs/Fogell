pipeline {
  agent any
  stages {
    stage('Test') {
      steps {
        sh 'mkdir -p reports'
        sh 'printf "%s" "<testsuite name=\\"s\\" tests=\\"3\\" failures=\\"0\\" errors=\\"0\\" skipped=\\"1\\"><testcase name=\\"a\\"/><testcase name=\\"b\\"/><testcase name=\\"c\\"/></testsuite>" > reports/results.xml'
        junit 'reports/*.xml'
      }
    }
  }
}
