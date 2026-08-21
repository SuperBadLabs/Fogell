pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def safeRows = [[child: [name: 'a', count: 1]], [child: [name: 'b', count: 10]]]
          safeRows*.child.first()?.name = 'safe'
          safeRows*.child.first()?.count += 2
          safeRows*.child.first()?.count++
          safeRows*.child.first()?.count--
          echo "safe-map:${safeRows[0].child.name}:${safeRows[1].child.name}:${safeRows[0].child.count}:${safeRows[1].child.count}"

          def nullRows = [[child: null], [child: [name: 'b']]]
          try {
            nullRows*.child.first()?.name = 'x'
            echo "safe-null:unexpected:${nullRows[0].child}:${nullRows[1].child.name}"
          } catch (Exception e) {
            echo "safe-null:caught:${e.class.simpleName}:${nullRows[0].child}:${nullRows[1].child.name}"
          }

          def scalarRows = [[child: 'text']]
          try {
            scalarRows*.child.first()?.name = 'x'
            echo "safe-scalar:unexpected:${scalarRows[0].child}"
          } catch (Exception e) {
            echo "safe-scalar:caught:${e.class.simpleName}:${scalarRows[0].child}"
          }

          def listRows = [[children: [[name: 'a0'], [name: 'a1']]], [children: [[name: 'b0'], [name: 'b1']]]]
          listRows*.children.first()[0] = [name: 'list-x']
          echo "method-list-index:${listRows[0].children[0].name}:${listRows[0].children[1].name}:${listRows[1].children[0].name}"

          def nullIndexRows = [[children: null], [children: [[name: 'b0']]]]
          try {
            nullIndexRows*.children.first()[0] = [name: 'null-x']
            echo "method-null-index:unexpected:${nullIndexRows[0].children}:${nullIndexRows[1].children[0].name}"
          } catch (Exception e) {
            echo "method-null-index:caught:${e.class.simpleName}:${nullIndexRows[0].children}:${nullIndexRows[1].children[0].name}"
          }

          def mapRows = [[holder: [slot: 'a']], [holder: [slot: 'b']]]
          mapRows*.holder.first()['slot'] = 'map-x'
          echo "method-map-index:${mapRows[0].holder.slot}:${mapRows[1].holder.slot}"

          def ordinaryMap = [slot: 'ordinary']
          ordinaryMap['slot'] = 'ordinary-x'
          echo "ordinary-map-index:${ordinaryMap.slot}"

          try {
            echo('safe-null-target')?.name = echo('safe-null-rhs')
          } catch (NullPointerException e) {
            echo 'safe-null-order:caught'
          }

          try {
            'text'?.name = echo('safe-missing-rhs')
          } catch (MissingPropertyException e) {
            echo 'safe-missing-order:caught'
          }
        }
      }
    }
  }
}
