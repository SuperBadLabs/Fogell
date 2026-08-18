// FG-186/FG-176 COMPOSED, the recovering half: a shell step that fails once
// and succeeds on the second attempt is a SUCCESS build with two markers.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    retry(3) {
                        sh 'echo a >> attempts.txt; test $(wc -l < attempts.txt) -ge 2'
                    }
                }
            }
        }
    }
}
