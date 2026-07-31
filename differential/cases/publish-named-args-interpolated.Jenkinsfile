// FG-100. The NAMED form of a publishing step must interpolate like the positional
// form. `archiveArtifacts "${DIR}/**"` was rendered; `archiveArtifacts artifacts:
// "${DIR}/**"` was not, and it fails QUIETLY — as "no artifacts matched" rather than
// an error naming a literal ${DIR}.
//
// The report is the same shape as `junit-failing` (one failing case, so both engines
// land on UNSTABLE) precisely so this case turns on the ARGUMENT rendering and not on
// junit's own result semantics.
pipeline {
  agent any
  environment {
    DIR = 'reports'
    REPORT = 'results.xml'
  }
  stages {
    stage('Test') {
      steps {
        sh 'mkdir -p reports'
        sh 'printf "%s" "<testsuite name=\\"s\\" tests=\\"2\\" failures=\\"1\\" errors=\\"0\\" skipped=\\"0\\"><testcase name=\\"ok\\"/><testcase name=\\"bad\\"><failure message=\\"boom\\"/></testcase></testsuite>" > reports/results.xml'
        archiveArtifacts artifacts: "${env.DIR}/${env.REPORT}"
        junit testResults: "${env.DIR}/${env.REPORT}"
      }
    }
  }
}
