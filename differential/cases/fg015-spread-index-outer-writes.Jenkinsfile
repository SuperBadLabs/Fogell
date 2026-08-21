pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def rows = [
            [child: [name: 'a', count: 1], children: [[name: 'a0'], [name: 'a1']]],
            [child: [name: 'b', count: 10], children: [[name: 'b0'], [name: 'b1']]]
          ]

          rows*.child[0].name = 'plain'
          echo "plain:${rows[0].child.name}:${rows[1].child.name}"

          rows*.child[0]?.name = 'safe'
          echo "safe:${rows[0].child.name}:${rows[1].child.name}"

          rows*.child[0].count += 2
          rows*.child[0].count++
          rows*.child[0].count--
          echo "compound:${rows[0].child.count}:${rows[1].child.count}"

          rows*.children[0][1].name = 'nested'
          echo "nested:${rows[0].children[1].name}:${rows[1].children[1].name}"

          rows*.children[0].first().name = 'method'
          echo "method:${rows[0].children[0].name}:${rows[1].children[0].name}"
        }
      }
    }
  }
}
