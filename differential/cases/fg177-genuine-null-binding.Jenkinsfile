// FG-177 slice 2. Lifting the value-use gate never bypasses the shared call
// validator: warning rows continue and return null; constructor-map and required
// binding faults remain typed/catchable; a failed shell never becomes null.
pipeline {
    agent any
    stages {
        stage('null') {
            steps {
                script {
                    def warned = sh(script: 'true', fogellProbeUnknown: true)
                    if (warned == null) {
                        sh 'printf warn > warning-null.txt'
                    }

                    try {
                        def bad = echo(message: 'never', fogellProbeUnknown: true)
                        sh 'touch escaped-constructor.txt'
                    } catch (IllegalArgumentException expected) {
                        sh 'printf constructor > constructor-caught.txt'
                    }

                    try {
                        def missing = stash()
                        sh 'touch escaped-required.txt'
                    } catch (IllegalArgumentException expected) {
                        sh 'printf required > required-caught.txt'
                    }

                    try {
                        def failed = sh(script: 'exit 3')
                        sh 'touch escaped-shell.txt'
                    } catch (Exception expected) {
                        sh 'printf shell > shell-caught.txt'
                    }
                }
            }
        }
    }
}
