pipeline {
  agent any
  options { skipDefaultCheckout() }
  stages {
    stage('probe') {
      steps {
        script {
          def selectedBranch = (env.BUILD_NUMBER in ['4', '5']) ? 'fg177-retained/20260822T052303Z-dec17753c7f1fb186faf05cc5b7feeb2/git/feature' : 'fg177-retained/20260822T052303Z-dec17753c7f1fb186faf05cc5b7feeb2/git/main'
          dir('checkout') {
            def value = git(url: 'git://100.105.179.51/repo.git', branch: selectedBranch)
            fg177Probe(value, 'git')
          }
          if (env.BUILD_NUMBER == '2') { error('FG177 intentional post-capture failure') }
        }
      }
    }
  }
  post { always { archiveArtifacts artifacts: 'checkout/fg177-workspace-*.txt', allowEmptyArchive: false } }
}

void fg177Probe(def value, String producer) {
  String revision = sh(script: 'git rev-parse HEAD', returnStdout: true).trim()
  writeFile file: 'fg177-workspace-revision.txt', text: revision + '\n'
  sh 'cp payload.txt fg177-workspace-payload.txt'
  echo "FG177 MAP PRODUCER=${producer} BUILD=${env.BUILD_NUMBER} CLASS=${value == null ? 'null' : value.getClass().getName()}"
  echo "FG177 MAP RENDER=${value}"
  echo "FG177 MAP KEYS=${value.keySet().sort().join(',')}"
  value.keySet().sort().each { key -> echo "FG177 MAP ENTRY=${key}|${value[key].getClass().getName()}|${value[key]}" }
  ['GIT_PREVIOUS_COMMIT', 'GIT_PREVIOUS_SUCCESSFUL_COMMIT'].each { key ->
    echo "FG177 HISTORY KEY=${key}|PRESENT=${value.containsKey(key)}|VALUE=${value[key]}"
  }
  def dynamicKey = 'GIT_COMMIT'
  echo "FG177 ACCESS PROPERTY=${value.GIT_COMMIT}|INDEX=${value['GIT_COMMIT']}|DYNAMIC=${value[dynamicKey]}"
  echo "FG177 MISSING PROPERTY=${value.FG177_MISSING}|INDEX=${value['FG177_MISSING']}|GET=${value.get('FG177_MISSING')}|CONTAINS=${value.containsKey('FG177_MISSING')}"
  ['integer': 0, 'null': null].each { label, index ->
    try { echo "FG177 WRONG-INDEX ${label}=VALUE:${value[index]}" }
    catch (Throwable problem) { echo "FG177 WRONG-INDEX ${label}=THREW:${problem.getClass().getName()}" }
  }
}
