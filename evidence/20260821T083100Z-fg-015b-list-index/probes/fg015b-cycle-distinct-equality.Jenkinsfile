pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
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
