// FG-129/FG-126a. A Jenkinsfile the pinned reference compiler refuses before
// the first Pipeline graph annotation. Fogell rejects the same decoded-literal
// defect during admission. The receipt compares the typed refusal disposition,
// terminal result, and real workspace hash; compiler wording is deliberately
// outside the compatibility claim.
pipeline {
    agent any
    stages {
        stage('must-not-run') {
            steps {
                sh 'printf "[\8]\n"'
            }
        }
    }
}
