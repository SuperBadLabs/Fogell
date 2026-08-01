//// SCM JOB ////
// FG-052. `checkout scm` in an SCM-defined job. MEASURED anatomy: Jenkins
// narrates "Obtained Jenkinsfile from git <url>", auto-inserts a
// "Declarative: Checkout SCM" stage (GitSCM clone shape — detached, no
// re-branch cluster, "Selected Git installation does not exist. Using
// Default" first, First-time tail), then the user's explicit `checkout scm`
// re-fetches with NO tail (changelog narrates once per build). The sh step
// reads a file only the checkout can provide; the workspace hash covers the
// checked-out tree.
pipeline {
    agent any
    stages {
        stage('co') {
            steps {
                checkout scm
                sh 'cat src/a.txt'
            }
        }
    }
}
