// FG-160. Jenkins fails a body-less `dir` with "dir step must be called with a body".
// Fogell CREATED the directory and exited 0: `findWrapperCalls` refused the spelling WITH
// a body and left the one without it running — half a shape refused. Block-taking steps
// are now absent from the script vocabulary entirely, so both spellings refuse, and this
// case proves the two engines AGREE ON THE REJECTION rather than merely not crashing.
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
