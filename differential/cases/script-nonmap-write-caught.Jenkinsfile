// FG-193's fault-class companion. A property write on a NON-MAP receiver throws
// on both engines, and a script-level catch INTERCEPTS it on both — the fault
// must be catchable, not a refusal. The first spelling of the strict arm raised
// an uncatchable Unsupported and diverged here while the receipts were green;
// the verifier caught it by asking what CLASS the fault was, not whether it
// fired. Sibling case script-null-write-caught covers the null receiver.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    try {
                        def s = 'str'
                        s.FOO = 'x'
                        echo 'no-throw'
                    } catch (Exception e) {
                        echo 'caught'
                    }
                    echo 'after'
                }
            }
        }
    }
}
