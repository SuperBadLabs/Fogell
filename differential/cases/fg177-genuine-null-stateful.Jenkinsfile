// FG-177 slice 2. Executor-backed archive and direct walker stash/delete/unstash
// all publish null only after their stateful work succeeds.
pipeline {
    agent any
    stages {
        stage('null') {
            steps {
                script {
                    sh 'printf seed > seed.txt'
                    def archived = archiveArtifacts(artifacts: 'seed.txt')
                    def stashed = stash(name: 'fg177-null', includes: 'seed.txt')
                    def deleted = deleteDir()
                    def restored = unstash(name: 'fg177-null')

                    if (archived == null && stashed == null && deleted == null && restored == null) {
                        sh 'printf pass > stateful-null.txt'
                    } else {
                        sh 'printf wrong > wrong-stateful-null.txt'
                    }
                }
            }
        }
    }
}
