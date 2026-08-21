pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def rows = [[child: null], [child: [name: 'b']]]
          try {
            rows*.child[0]?.name = 'safe-null'
            echo "safe-null:unexpected-success:${rows[0].child}:${rows[1].child.name}"
          } catch (Exception e) {
            echo "safe-null:caught:${rows[0].child}:${rows[1].child.name}"
          }
        }
      }
    }
  }
}
