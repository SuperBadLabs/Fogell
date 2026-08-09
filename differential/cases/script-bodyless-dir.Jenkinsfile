// FG-160. Jenkins fails a body-less `dir` with "dir step must be called with a body".
// Fogell CREATED the directory and exited 0: `findWrapperCalls` refused the spelling WITH
// a body and left the one without it running — half a shape refused. Block-taking steps
// are now absent from the script vocabulary entirely, so both spellings refuse.
//
// WHAT THIS CASE PROVES, narrowed after review: the two engines agree on the TERMINAL
// RESULT and the OUTPUT. It does NOT prove they agree on side effects, because the
// workspace manifest hashes FILES and an extra EMPTY DIRECTORY is invisible to it — this
// receipt would be byte-identical with `child/` left behind. It said it proved agreement
// on the rejection, which was more than it could see. The refusal now happens BEFORE
// `Directory.CreateDirectory`, verified by hand rather than by this case; FG-173 carries
// making the checker able to see it.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    dir('child')
                }
            }
        }
    }
}
