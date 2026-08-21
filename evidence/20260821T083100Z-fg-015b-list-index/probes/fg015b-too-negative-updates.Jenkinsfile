pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def compoundValues = [1]
          def compoundReceiver = { echo 'compound-negative:receiver'; compoundValues }
          def compoundIndex = { echo 'compound-negative:index'; -2 }
          def compoundRhs = { echo 'compound-negative:rhs'; 2 }
          try {
            compoundReceiver()[compoundIndex()] += compoundRhs()
            echo 'compound-negative:unexpected'
          } catch (Throwable e) {
            echo "compound-negative:caught:${e.class.simpleName}:${compoundValues}"
          }

          def incrementValues = [1]
          def incrementReceiver = { echo 'increment-negative:receiver'; incrementValues }
          def incrementIndex = { echo 'increment-negative:index'; -2 }
          try {
            incrementReceiver()[incrementIndex()]++
            echo 'increment-negative:unexpected'
          } catch (Throwable e) {
            echo "increment-negative:caught:${e.class.simpleName}:${incrementValues}"
          }
        }
      }
    }
  }
}
