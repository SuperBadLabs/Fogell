pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def rows = [
            [child: [name: 'a'], count: 1, children: [[name: 'a0'], [name: 'a1']]],
            [child: [name: 'b'], count: 10, children: [[name: 'b0'], [name: 'b1']]]
          ]

          try {
            rows*.child[0] = [name: 'direct']
            echo "direct:ok:${rows[0].child.name}:${rows[1].child.name}"
          } catch (Exception e) {
            echo "direct:caught:${e.class.simpleName}:${rows[0].child.name}:${rows[1].child.name}"
          }

          try {
            rows*.child[0] += [extra: 'compound']
            echo "compound:ok:${rows[0].child.extra}:${rows[1].child.extra}"
          } catch (Exception e) {
            echo "compound:caught:${e.class.simpleName}:${rows[0].child.extra}:${rows[1].child.extra}"
          }

          try {
            rows*.count[0]++
            echo "increment:ok:${rows[0].count}:${rows[1].count}"
          } catch (Exception e) {
            echo "increment:caught:${e.class.simpleName}:${rows[0].count}:${rows[1].count}"
          }

          try {
            rows*.count[0]--
            echo "decrement:ok:${rows[0].count}:${rows[1].count}"
          } catch (Exception e) {
            echo "decrement:caught:${e.class.simpleName}:${rows[0].count}:${rows[1].count}"
          }

          try {
            rows*.children[0][1] = [name: 'nested-direct']
            echo "nested:ok:${rows[0].children[1].name}:${rows[1].children[1].name}"
          } catch (Exception e) {
            echo "nested:caught:${e.class.simpleName}:${rows[0].children[1].name}:${rows[1].children[1].name}"
          }
        }
      }
    }
  }
}
