// FG-172. A wrapper's body inside a `script { }` runs INSIDE the wrapper. Under the batch
// model the interpreter evaluated the trailing closure immediately and flattened it, so
// `dir('sub')` arrived with no body and `sh` ran in the stage root while the build
// reported success.
//
// The second `sh` is not decoration: it proves the context is RESTORED. A runner that
// established `sub` and never unwound would place both files there, and a case with only
// the first step could not tell the two apart. Only the workspace manifest's PATHS catch
// this — the file CONTENT is identical either way, which is exactly how the original
// `dir` defect hid.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                script {
                    dir('sub') {
                        sh 'echo placed > where.txt'
                    }
                    sh 'echo outside > top.txt'
                }
            }
        }
    }
}
