// FG-048b review fix, PR #16 round 4. The data-bound parameter name for `changeset` and
// `changelog` is `pattern`.
//
// MEASURED after I invented `glob`/`regexp` without checking: Jenkins ACCEPTS
// `changeset pattern:` and REJECTS `changeset glob:` outright with a compilation error.
// The invented key inverted the gate in both directions at once — accepting a form Jenkins
// refuses, while failing closed on the form real Jenkinsfiles use.
pipeline {
    agent any
    stages {
        stage('changeset pattern') {
            when { changeset pattern: '**/*.java' }
            steps { sh 'echo ran > changeset.txt' }
        }
        stage('changelog pattern') {
            when { changelog pattern: '.*fix.*' }
            steps { sh 'echo ran > changelog.txt' }
        }
        stage('triggeredBy cause') {
            when { triggeredBy cause: 'TimerTrigger' }
            steps { sh 'echo ran > triggeredBy.txt' }
        }
        stage('control') {
            steps { sh 'echo control > control.txt' }
        }
    }
}
