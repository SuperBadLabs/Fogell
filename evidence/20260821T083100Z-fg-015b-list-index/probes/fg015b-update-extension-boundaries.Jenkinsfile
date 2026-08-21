pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def events = []
          def atSizeRhs = { events << 'at-size-compound-rhs'; 2 }
          def beyondRhs = { events << 'beyond-compound-rhs'; 2 }

          def atSize = [1]
          try {
            atSize[1] += atSizeRhs()
            echo "at-size-compound:success:${atSize}"
          } catch (Throwable e) {
            echo "at-size-compound:caught:${e.class.simpleName}:${atSize}"
          }

          def beyond = [1]
          try {
            beyond[3] += beyondRhs()
            echo "beyond-compound:success:${beyond}"
          } catch (Throwable e) {
            echo "beyond-compound:caught:${e.class.simpleName}:${beyond}"
          }

          def emptyInc = []
          try {
            emptyInc[0]++
            echo "empty-inc:success:${emptyInc}"
          } catch (Throwable e) {
            echo "empty-inc:caught:${e.class.simpleName}:${emptyInc}"
          }

          def atSizeInc = [1]
          try {
            atSizeInc[1]++
            echo "at-size-inc:success:${atSizeInc}"
          } catch (Throwable e) {
            echo "at-size-inc:caught:${e.class.simpleName}:${atSizeInc}"
          }

          def beyondDec = [1]
          try {
            beyondDec[3]--
            echo "beyond-dec:success:${beyondDec}"
          } catch (Throwable e) {
            echo "beyond-dec:caught:${e.class.simpleName}:${beyondDec}"
          }

          def atSizeString = [1]
          try {
            atSizeString[1] += 'x'
            echo "at-size-string:success:${atSizeString}"
          } catch (Throwable e) {
            echo "at-size-string:caught:${e.class.simpleName}:${atSizeString}"
          }

          echo "extension-update-events:${events}"
        }
      }
    }
  }
}
