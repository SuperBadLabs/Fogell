pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def list = [null]
          def map = [list: list]
          list[0] = map
          try {
            echo "list-map-display:${list}"
          } catch (Throwable e) {
            echo "list-map-display:caught:${e.class.simpleName}"
          }
        }
      }
    }
  }
}
