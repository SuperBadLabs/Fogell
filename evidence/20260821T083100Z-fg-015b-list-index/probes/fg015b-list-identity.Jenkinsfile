pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def xs = ['a', 'b']
          def alias = xs
          def assign = { xs[0] = 'x' }
          def assigned = assign()
          echo "basic:${assigned}:${xs}:${alias}"

          def closureAlias = xs
          def mutateAlias = { closureAlias[1] = 'c' }
          mutateAlias()
          echo "closure:${xs}:${alias}:${closureAlias}"

          def nested = [['n0', 'n1'], ['m0', 'm1']]
          def first = nested[0]
          nested[0][1] = 'nested-x'
          echo "nested:${nested}:${first}"

          def rows = [
            [child: [name: 'a'], children: [[name: 'a0'], [name: 'a1']]],
            [child: [name: 'b'], children: [[name: 'b0'], [name: 'b1']]]
          ]
          rows*.child[0] = [name: 'temporary']
          echo "projection-temp:${rows[0].child.name}:${rows[1].child.name}"
          rows*.children[0][1] = [name: 'nested-direct']
          echo "projection-nested:${rows[0].children[1].name}:${rows[1].children[1].name}"
          def nums = [1, 10]
          nums[0] += 2
          def incResult = nums[0]++
          def decResult = nums[0]--
          echo "compound:${incResult}:${decResult}:${nums}"

          def orderValues = [0]
          def orderReceiver = { echo 'order:receiver'; orderValues }
          def orderIndex = { echo 'order:index'; 0 }
          def orderRhs = { echo 'order:rhs'; 9 }
          orderReceiver()[orderIndex()] = orderRhs()
          echo "order:after:${orderValues}"

          def ordinaryMap = [slot: 'a']
          ordinaryMap['slot'] = 'map-x'
          echo "map-control:${ordinaryMap.slot}"
        }
      }
    }
  }
}
