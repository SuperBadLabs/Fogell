// FG-137 BOUNDARY. `retry(1)` with a `post` block RUNS — it is not refused.
//
// The FG-137 guard exists because Jenkins runs a retried stage's `post` once per
// ATTEMPT and this engine runs it once, so a stage with retry(2)+post is refused
// rather than under-running post side effects. `retry(1)` is ONE total attempt,
// so per-attempt and once are the same thing and there is nothing to under-run.
//
// The first version of that guard refused EVERY retry+post stage including this
// one — over-refusing a pipeline Jenkins runs correctly, which is the FG-126
// trap in a new costume and one introduced while trying to prevent a divergence.
//
// This case pins the boundary. Without it the guard could be widened back to all
// retry+post stages and nothing would object.
pipeline {
    agent any
    stages {
        stage('one') {
            options { retry(1) }
            steps { sh 'echo ran > out.txt' }
            post { always { sh 'echo tick >> postticks.txt' } }
        }
    }
}
