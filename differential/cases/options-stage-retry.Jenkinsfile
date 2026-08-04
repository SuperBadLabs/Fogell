// FG-053(b). Stage-level `options { retry(N) }` re-runs the stage's STEPS.
//
// MEASURED on Jenkins 2.568.1: attempt 1 fails, `Retrying` is printed with no
// delay, attempt 2 succeeds, the following stage RUNS and the build SUCCEEDS.
//
// The counter is a file, which is the point: workspace state PERSISTS across
// attempts, so `n` carries over rather than restarting. An implementation that
// reset the workspace between attempts would still print the right lines and
// fail this case on the workspace hash.
//
// Refused by FG-053(a) until this landed, so that it could not run with the
// wrong semantics silently.
pipeline {
    agent any
    stages {
        stage('flaky') {
            options { retry(3) }
            steps {
                sh 'n=$(cat count.txt 2>/dev/null || echo 0); n=$((n+1)); echo $n > count.txt; echo attempt $n; [ $n -ge 2 ]'
            }
        }
        stage('after') {
            steps { sh 'echo after > after.txt' }
        }
    }
}
