pipeline {
  agent any
  stages {
    stage('Test failure') {
      steps {
        sh "printf '%s' '<testsuite name=\"Example\"><testcase name=\"planted_failure\" classname=\"Example.Tests\" file=\"tests/example.fs\" line=\"23\"><failure message=\"expected 2 got 1\">assertion failed</failure></testcase></testsuite>' > results.xml"
        junit 'results.xml'
      }
    }
  }
}
