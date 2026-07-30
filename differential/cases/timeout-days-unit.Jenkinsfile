// FG-034 review fix, proof. `timeout(time: 30, unit: 'DAYS')` is 2,592,000,000 ms,
// past Int32.MaxValue. The deadline arithmetic narrowed int64 -> int32, wrapped
// negative, and was floored to 1 ms — so this pipeline ABORTED instantly.
// Fixing "DAYS silently means minutes" had introduced "DAYS means one
// millisecond". Caught by a Codex review comment on PR #13; the build simply
// has to succeed.
pipeline {
    agent any
    stages {
        stage('Long budget') {
            steps {
                timeout(time: 30, unit: 'DAYS') {
                    sh 'echo finished > done.txt'
                }
            }
        }
    }
}
