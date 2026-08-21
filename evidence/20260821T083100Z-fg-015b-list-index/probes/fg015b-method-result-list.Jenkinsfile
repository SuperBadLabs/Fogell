pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def rows = [
            [children: [[name: 'a0'], [name: 'a1']]],
            [children: [[name: 'b0'], [name: 'b1']]]
          ]
          rows*.children.first()[0] = [name: 'method-direct']
          echo "method-result:${rows[0].children[0].name}:${rows[1].children[0].name}"
        }
      }
    }
  }
}
