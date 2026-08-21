// FG-177 slice 2. Plain/false-flag sh, zero-argument echo and successful
// unstable return genuine Groovy null. Unstable remains nonterminal.
pipeline {
    agent any
    stages {
        stage('null') {
            steps {
                script {
                    def plain = sh(script: 'true')
                    def falseFlags = sh(script: 'true', returnStdout: false, returnStatus: false)
                    def echoed = echo()
                    def unstableValue = unstable(message: 'fg177 genuine null')

                    if (plain == null && falseFlags == null && echoed == null && unstableValue == null) {
                        sh 'printf pass > basic-null.txt'
                    } else {
                        sh 'printf wrong > wrong-basic-null.txt'
                    }
                }
            }
        }
    }
}
