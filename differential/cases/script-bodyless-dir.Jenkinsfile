// FG-160. Jenkins fails a body-less `dir` with "dir step must be called with a body".
// Fogell CREATED the directory and exited 0: `findWrapperCalls` refused the spelling WITH
// a body and left the one without it running — half a shape refused. Block-taking steps
// with a body and left the one without it running — half a shape refused. `dir` is
// admitted again under FG-172 (its arm runs a hosted body), so the body-less spelling
// is refused by the ARM, before the directory is created.
//
// FG-173 makes physical empty leaf directories part of the compared workspace state.
// This case now proves the two engines agree on the terminal result, output, AND absence
// of a leftover `child/`: either engine creating it changes that side's workspace hash
// and prints the directory row in the receipt. Directory symlinks are never followed.
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
