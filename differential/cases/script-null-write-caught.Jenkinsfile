// FG-193's fault-class companion, null receiver: Groovy throws NPE, a script
// catch intercepts it, the build runs on — on both engines. See
// script-nonmap-write-caught for the class of defect this pins.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    try {
                        def s = null
                        s.FOO = 'x'
                        echo 'no-throw'
                    } catch (Exception e) {
                        echo 'caught-other'
                    }
                    echo 'after'
                }
            }
        }
    }
}
