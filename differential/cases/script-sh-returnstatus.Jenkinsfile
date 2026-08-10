// FG-174. `sh(returnStatus: true)` yields the exit code and DOES NOT FAIL THE BUILD.
// Getting this wrong turns a deliberate status check into a failed build; getting it
// wrong the other way hides a real failure.
//
// THE TYPE IS PART OF THE BEHAVIOUR, not an implementation detail. Jenkins hands back a
// Groovy Integer, so `code == 7` is true. An engine returning the STRING "7" compares
// false and takes the other branch while reporting success — a wrong answer delivered
// quietly, which is why the comparison writes its result into the workspace instead of
// merely printing it. `int.txt` is how a String-returning engine is caught.
//
// `after.txt` proves the build CONTINUED. A companion to `script-returnstatus-timeout`,
// which proves the opposite half: this flag converts a SHELL EXIT into a value, and
// never a wrapper interruption.
pipeline {
    agent any
    stages {
        stage('Status') {
            steps {
                script {
                    def code = sh(script: 'exit 7', returnStatus: true)
                    echo "code:${code}"
                    if (code == 7) {
                        sh 'echo integer > int.txt'
                    } else {
                        sh 'echo not-an-integer > int.txt'
                    }
                    sh 'echo reached > after.txt'
                }
            }
        }
    }
}
