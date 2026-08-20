//// SCM JOB ////
// FG-177 measurement only: checkout scm's unknown-key policy, missing-argument
// policy, return class, complete key set, entry types, and entry values.
pipeline {
    agent any
    options { skipDefaultCheckout() }
    stages {
        stage('probe') {
            steps {
                script {
                    try {
                        checkout()
                        echo 'FG177 MISSING checkout CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 MISSING checkout THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        checkout(fogellProbeUnknown: true)
                        echo 'FG177 UNKNOWN-ONLY checkout CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN-ONLY checkout THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    try {
                        checkout(scm: scm, fogellProbeUnknown: true)
                        sh 'printf after > fg177-unknown-checkout-after.txt'
                        echo 'FG177 UNKNOWN checkout CONTINUED'
                    } catch (Exception e) {
                        echo "FG177 UNKNOWN checkout THREW ${e.getClass().getName()}: ${e.getMessage()}"
                    }

                    def checkoutValue = checkout scm
                    echo "FG177 RETURN checkout CLASS=${checkoutValue == null ? 'null' : checkoutValue.getClass().getName()} VALUE=${checkoutValue}"
                    if (checkoutValue instanceof Map) {
                        echo "FG177 RETURN checkout KEYS=${checkoutValue.keySet().sort().join(',')}"
                        checkoutValue.keySet().sort().each { k -> echo "FG177 RETURN checkout ENTRY ${k} CLASS=${checkoutValue[k] == null ? 'null' : checkoutValue[k].getClass().getName()} VALUE=${checkoutValue[k]}" }
                    }
                    sh 'printf done > fg177-return-checkout-complete.txt'
                }
            }
        }
    }
}
