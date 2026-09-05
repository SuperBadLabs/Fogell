pipeline {
  agent any
  stages {
    stage('Toolchain') {
      steps {
        sh 'JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64 /home/srikanth/fogell-build-jenkins/maven/bin/mvn --version'
      }
    }
    stage('Build core') {
      steps {
        sh 'cd /home/srikanth/fogell-build-jenkins/src && JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64 /home/srikanth/fogell-build-jenkins/maven/bin/mvn -B -o -pl core -am clean package -DskipTests -Dspotbugs.skip=true -Dcheckstyle.skip=true -Denforcer.skip=true -Ddevelocity.cache.local.enabled=false'
      }
    }
    stage('Verify artifact') {
      steps {
        sh 'ls -la /home/srikanth/fogell-build-jenkins/src/core/target/jenkins-core-2.577.jar'
      }
    }
  }
}
