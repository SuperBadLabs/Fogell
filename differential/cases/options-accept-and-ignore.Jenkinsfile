// FG-053. The accept-and-ignore family: retention and queueing policy that has
// no observable effect on a SINGLE build.
//
// `buildDiscarder` (18 corpus files) and `disableConcurrentBuilds` (6) are the
// two biggest option users in the corpus, and neither changes what one build
// does — they govern how many builds are kept and whether two may run at once.
// `quietPeriod` and `rateLimitBuilds` are the same shape: they affect when a
// build STARTS, which a receipt comparing one build cannot see.
//
// So the whole job is to accept them and say why.
//
// WHAT THIS CASE DOES NOT PROVE, stated because an earlier version of this
// comment claimed it did: it covers the VALID form of each option only. A known
// name with a malformed argument — `quietPeriod('abc')`, `buildDiscarder()`,
// `disableConcurrentBuilds('x')`, `parallelsAlwaysFailFast(false)` — is still
// accepted here and refused by Jenkins. That is FG-130, and it is a THIRD axis
// of the same conflation this ticket has now hit twice: the name is valid, the
// scope is valid, and the ARGUMENTS are not.
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
