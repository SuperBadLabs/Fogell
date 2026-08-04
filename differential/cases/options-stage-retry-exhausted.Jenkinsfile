// FG-053(b). Stage-level retry when every attempt fails.
//
// MEASURED on Jenkins 2.568.1: `retry(2)` is TWO TOTAL attempts, not two after
// the first, with one `Retrying` between them and no backoff. The stage then
// fails for good: the following stage is skipped, pipeline `post` STILL RUNS,
// and the build fails.
//
// The `post` half is the part worth pinning. A stage failing after its retries
// are spent is an ordinary build failure, NOT a compile rejection, so `post`
// must run — the opposite of the compile-shaped refusals, where it must not.
pipeline {
    agent any
    stages {
        stage('always-fails') {
            options { retry(2) }
            steps { sh 'echo tick >> ticks.txt; exit 9' }
        }
        stage('after') {
            steps { sh 'echo after > after.txt' }
        }
    }
    post { always { sh 'echo post > post.txt' } }
}
