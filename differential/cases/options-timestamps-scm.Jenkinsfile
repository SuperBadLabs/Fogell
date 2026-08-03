//// SCM JOB ////
// FG-053. `timestamps()` in an SCM-DEFINED job. MEASURED anatomy: Jenkins must
// FETCH and PARSE the Jenkinsfile before a Declarative option exists to
// activate, so its "Obtained Jenkinsfile from git ..." provenance line AND its
// auto-inserted checkout are both UNPREFIXED — stamping begins with the build's
// own step output. Both engines read PARTIAL (1/21) here.
//
// Enabling the wrapper at context creation stamped that provenance line too, so
// Fogell read `all` against Jenkins' `partial` and the case failed on coverage
// while its output and workspace agreed — a divergence invented by where the
// wrapper was switched on. This case is the difference between reasoning about
// that and measuring it.
pipeline {
    agent any
    options { timestamps() }
    stages {
        stage('one') {
            steps {
                sh 'echo scm-stamped > scm.txt'
            }
        }
    }
}
