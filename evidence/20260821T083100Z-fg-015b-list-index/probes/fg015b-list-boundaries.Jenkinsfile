pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def rhs = { label, value -> echo "rhs:${label}"; value }

          def negative = ['a', 'b']
          negative[-1] = rhs('negative', 'neg-x')
          echo "negative:${negative}"

          def empty = []
          empty[0] = rhs('empty-zero', 'zero')
          echo "empty-zero:${empty}"

          def atSize = ['a']
          atSize[1] = rhs('at-size', 'size-x')
          echo "at-size:${atSize}"

          def beyond = ['a']
          beyond[3] = rhs('beyond', 'far-x')
          echo "beyond:${beyond}"

          def tooNegative = ['a']
          try {
            tooNegative[-2] = rhs('too-negative', 'bad')
            echo "too-negative:unexpected:${tooNegative}"
          } catch (Throwable e) {
            echo "too-negative:caught:${e.class.simpleName}:${tooNegative}"
          }

          def nullKey = ['a']
          try {
            nullKey[null] = rhs('null-key', 'bad')
            echo "null-key:unexpected:${nullKey}"
          } catch (Throwable e) {
            echo "null-key:caught:${e.class.simpleName}:${nullKey}"
          }

          def stringKey = ['a']
          try {
            stringKey['0'] = rhs('string-key', 'bad')
            echo "string-key:unexpected:${stringKey}"
          } catch (Throwable e) {
            echo "string-key:caught:${e.class.simpleName}:${stringKey}"
          }

          def rangeKey = ['a', 'b', 'c', 'd']
          try {
            rangeKey[1..2] = rhs('range-key', ['x', 'y'])
            echo "range-key:ok:${rangeKey}"
          } catch (Throwable e) {
            echo "range-key:caught:${e.class.simpleName}:${rangeKey}"
          }

          def nullReceiver = null
          try {
            nullReceiver[0] = rhs('null-receiver', 'bad')
            echo 'null-receiver:unexpected'
          } catch (Throwable e) {
            echo "null-receiver:caught:${e.class.simpleName}"
          }

          def scalarReceiver = 'ab'
          try {
            scalarReceiver[0] = rhs('scalar-receiver', 'bad')
            echo "scalar-receiver:unexpected:${scalarReceiver}"
          } catch (Throwable e) {
            echo "scalar-receiver:caught:${e.class.simpleName}:${scalarReceiver}"
          }

          def orderFail = ['a']
          def failReceiver = { echo 'fail-order:receiver'; orderFail }
          def failIndex = { echo 'fail-order:index'; -2 }
          def failRhs = { echo 'fail-order:rhs'; 'bad' }
          try {
            failReceiver()[failIndex()] = failRhs()
            echo 'fail-order:unexpected'
          } catch (Throwable e) {
            echo "fail-order:caught:${e.class.simpleName}:${orderFail}"
          }

          def noRhs = ['a']
          def throwingIndex = { echo 'index-throw:index'; throw new IllegalStateException('index') }
          def skippedRhs = { echo 'index-throw:rhs'; 'bad' }
          try {
            noRhs[throwingIndex()] = skippedRhs()
            echo 'index-throw:unexpected'
          } catch (Throwable e) {
            echo "index-throw:caught:${e.class.simpleName}:${noRhs}"
          }

          def mapControl = [slot: 'a']
          mapControl['slot'] = rhs('map-control', 'map-x')
          echo "map-control:${mapControl.slot}"
        }
      }
    }
  }
}
