pipeline {
    agent any
    stages {
        stage('Branch main') {
            when { branch 'main' }
            steps { sh 'echo branch-main-ran > branch.txt' }
        }
        stage('Tag v-star') {
            when { tag 'v*' }
            steps { sh 'echo tag-ran > tag.txt' }
        }
        stage('Equals match') {
            when { equals expected: 2, actual: 2 }
            steps { sh 'echo equals-matched' }
        }
        stage('Equals mismatch') {
            when { equals expected: 2, actual: 3 }
            steps { sh 'echo equals-mismatch-ran > equals.txt' }
        }
        stage('Always') {
            steps { sh 'echo always-ran' }
        }
    }
}
