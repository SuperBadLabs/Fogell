// FG-048 review fixes. Two findings from Codex on PR #13:
//  * A `when { }` with SIBLING conditions is an implicit allOf. Only the first was
//    parsed; the rest failed, the gate became unmodelled, and the build FAILED.
//  * Jenkins-provided variables such as BUILD_NUMBER were treated as absent, so a
//    gate referring to them skipped a stage Jenkins runs.
pipeline {
    agent any
    environment {
        FLAVOUR = 'full'
    }
    stages {
        stage('Both siblings match') {
            when {
                environment name: 'FLAVOUR', value: 'full'
                environment name: 'BUILD_NUMBER', value: '1'
            }
            steps {
                sh 'echo siblings-matched > siblings.txt'
            }
        }
        stage('One sibling fails') {
            when {
                environment name: 'FLAVOUR', value: 'full'
                environment name: 'BUILD_NUMBER', value: '999'
            }
            steps {
                sh 'echo MUST-NOT-RUN > wrong.txt'
            }
        }
        stage('Jenkins var via env dot') {
            when {
                expression { return env.BUILD_NUMBER == '1' }
            }
            steps {
                sh 'echo build-number-visible > buildnum.txt'
            }
        }
    }
}
