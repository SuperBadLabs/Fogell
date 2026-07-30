// FG-046 review fix, PR #17 round 3. Jenkins EVALUATES a GString before showing the
// prompt, so `input message: "Deploy ${TARGET}?"` displays the value. Emitting the
// parser's raw text diverged. A single-quoted argument stays literal — which is why the
// parser now records which named args were single-quoted. Shell steps never needed this
// because the shell performs its own expansion.
pipeline {
    agent any
    environment {
        TARGET = 'production'
    }
    stages {
        stage('Gate') {
            steps {
                timeout(time: 4, unit: 'SECONDS') {
                    input message: "Deploy to ${TARGET}?", ok: "Ship to ${TARGET}"
                }
            }
        }
    }
}
