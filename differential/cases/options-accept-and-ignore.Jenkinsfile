// FG-053. The accept-and-ignore family: retention and queueing policy that has
// no observable effect on a SINGLE build.
//
// `buildDiscarder` (18 corpus files) and `disableConcurrentBuilds` (6) are the
// two biggest option users in the corpus, and neither changes what one build
// does — they govern how many builds are kept and whether two may run at once.
// `quietPeriod` and `rateLimitBuilds` are the same shape: they affect when a
// build STARTS, which a receipt comparing one build cannot see.
//
// So the whole job is to accept them and say why. What this case pins is that
// accepting them is not silently accepting ANYTHING: `options-unknown-name`
// proves an unknown type is refused, and these four are on Jenkins' own list.
pipeline {
    agent any
    options {
        buildDiscarder(logRotator(numToKeepStr: '5'))
        disableConcurrentBuilds()
        quietPeriod(0)
        rateLimitBuilds(throttle: [count: 10, durationName: 'hour', userBoost: true])
    }
    stages {
        stage('one') {
            steps { sh 'echo ran > out.txt' }
        }
    }
}
