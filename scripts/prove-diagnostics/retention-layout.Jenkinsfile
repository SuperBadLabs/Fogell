pipeline {
  agent any
  stages {
    stage('Owned HOME and stash') {
      steps {
        sh 'printf "home-evidence" > "$HOME/fg268-home"; printf "stash-evidence" > fg268-stash'
        stash name: 'retention', includes: 'fg268-stash'
      }
    }
  }
}
