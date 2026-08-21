pipeline {
  agent any
  stages {
    stage('probe') {
      steps {
        script {
          def compoundValues = [1]
          def compoundReceiver = { echo 'compound-order:receiver'; compoundValues }
          def compoundIndex = { echo 'compound-order:index'; 0 }
          def compoundRhs = { echo 'compound-order:rhs'; 2 }
          compoundReceiver()[compoundIndex()] += compoundRhs()
          echo "compound-order:after:${compoundValues}"

          def incrementValues = [3]
          def incrementReceiver = { echo 'increment-order:receiver'; incrementValues }
          def incrementIndex = { echo 'increment-order:index'; 0 }
          incrementReceiver()[incrementIndex()]++
          echo "increment-order:after:${incrementValues}"

          def decrementValues = [4]
          def decrementReceiver = { echo 'decrement-order:receiver'; decrementValues }
          def decrementIndex = { echo 'decrement-order:index'; 0 }
          decrementReceiver()[decrementIndex()]--
          echo "decrement-order:after:${decrementValues}"
        }
      }
    }
  }
}
