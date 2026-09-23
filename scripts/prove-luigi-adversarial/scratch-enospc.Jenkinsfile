pipeline {
  agent any
  stages {
    stage('Dedicated temporary filesystem exhaustion') {
      steps {
        sh 'd=/tmp/fogell-campaign-__CAMPAIGN__; mkdir "$d" || exit 19; trap "rm -f $d/blob; rmdir $d" EXIT; LC_ALL=C dd if=/dev/zero of="$d/blob" bs=1048576 count=300 status=none'
      }
    }
  }
}
