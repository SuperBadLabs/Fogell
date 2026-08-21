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

          def list = [null]
          def map = [list: list]
          list[0] = map
          echo "list-map-display:${list}"

          def first = [null]
          first[0] = first
          def second = [null]
          second[0] = second
          try {
            echo "distinct-cycle-eq:${first == second}"
          } catch (Throwable e) {
            echo "distinct-cycle-eq:caught:${e.class.simpleName}"
          }
        }
      }
    }
  }
}
