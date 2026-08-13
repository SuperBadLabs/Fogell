// FG-172. A wrapper body reaches a call as `trailing` OR as a FINAL CLOSURE ARGUMENT,
// depending on which parser path matched: `dir('sub') { … }` versus `dir('sub', { … })`.
// The interpreter took bodies only from `trailing`, so this spelling stringified its
// closure into a positional argument and `dir` then rejected ITSELF as body-less.
//
// `StepValueUse.findWrapperCalls` had already been taught both spellings one round
// earlier; the interpreter had not. Normalising in the interpreter means every host sees
// one shape rather than each rediscovering the two — which is why this case sits beside
// `script-dir-body` instead of replacing it: they exercise the same semantics through
// different syntax, and only having both proves the normalisation rather than one path.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    dir('sub', { sh 'echo placed > where.txt' })
                    sh 'echo outside > top.txt'
                }
            }
        }
    }
}
