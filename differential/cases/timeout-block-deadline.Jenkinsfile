// FG-034 review fix. `timeout` bounds the WHOLE BLOCK, not each step in it.
// Two 2-second steps inside a 3-second timeout must NOT both finish: the first
// completes (`first.txt`), the block's deadline then expires during the second
// (`second.txt` absent). The first implementation handed the full budget to
// every step independently, so both succeeded and the block ran ~4 seconds —
// caught by a Codex review comment on PR #12.
pipeline {
    agent any
    stages {
        stage('Bounded block') {
            steps {
                timeout(time: 3, unit: 'SECONDS') {
                    sh 'sleep 2; echo one > first.txt'
                    sh 'sleep 2; echo two > second.txt'
                }
            }
        }
    }
}
