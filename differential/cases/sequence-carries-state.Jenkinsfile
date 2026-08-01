// FG-110. The multi-build lane's own load-bearing claims, receipt-proven:
// the WORKSPACE persists across a sequence (build 2 reads what build 1 wrote —
// a regression that wiped it per build would fail here, nowhere else) and
// BUILD_NUMBER increments on both engines (it was pinned to "1" on the Fogell
// side until this case's review round).
pipeline {
    agent any
    stages {
        stage('write') {
            steps {
                sh 'echo carried-from-one > carried.txt'
                sh 'echo build=$BUILD_NUMBER'
            }
        }
    }
}
//// NEXT BUILD ////
pipeline {
    agent any
    stages {
        stage('read') {
            steps {
                sh 'cat carried.txt'
                sh 'echo build=$BUILD_NUMBER'
            }
        }
    }
}
