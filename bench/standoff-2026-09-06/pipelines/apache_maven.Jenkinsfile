pipeline {
  agent any
  stages {
    stage('Toolchain') {
      steps {
        sh "JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64 /home/srikanth/fogell-build-jenkins/maven/bin/mvn --version"
      }
    }
    stage('Build') {
      steps {
        sh "cd /home/srikanth/standoff/apache_maven && JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64 /home/srikanth/fogell-build-jenkins/maven/bin/mvn -B -o -DskipTests -Dspotbugs.skip=true -Dcheckstyle.skip=true -Dmaven.javadoc.skip=true -Drat.skip=true -Dmaven.source.skip=true -Ddevelocity.cache.local.enabled=false -pl :maven-core -am clean package"
      }
    }
    stage('Verify artifact') {
      steps {
        sh "find /home/srikanth/standoff/apache_maven -path '*/target/*.jar' -print -quit | grep -q ."
      }
    }
  }
}
