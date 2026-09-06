pipeline {
  agent any
  stages {
    stage('Toolchain') {
      steps {
        sh "JAVA_HOME=/usr/lib/jvm/java-25-openjdk-amd64 /home/srikanth/fogell-build-jenkins/maven/bin/mvn --version"
      }
    }
    stage('Build') {
      steps {
        sh "cd /home/srikanth/standoff/apache_activemq && JAVA_HOME=/usr/lib/jvm/java-25-openjdk-amd64 /home/srikanth/fogell-build-jenkins/maven/bin/mvn -B -o -DskipTests -Dspotbugs.skip=true -Dcheckstyle.skip=true -Dmaven.javadoc.skip=true -Drat.skip=true -Dmaven.source.skip=true -Ddevelocity.cache.local.enabled=false -pl :activemq-client -am clean package"
      }
    }
    stage('Verify artifact') {
      steps {
        sh "find /home/srikanth/standoff/apache_activemq -path '*/target/*.jar' -print -quit | grep -q ."
      }
    }
  }
}
