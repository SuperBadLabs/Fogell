pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def self = [null]
          self[0] = self
          echo "self-display:${self}"
          echo "self-alias-eq:${self == self}"
        }
      }
    }
  }
}
