// FG-048b. The six `when` conditions whose truth depends on build CONTEXT a plain
// pipeline job does not have: an SCM changelog, multibranch metadata, a timer trigger, a
// restart. The filtered changeRequest stage is also FG-175's overshoot control: a valid
// condition shape Fogell does not fully model must remain admitted and skip here.
//
// MEASURED: on such a job every one of them is FALSE, its stage is skipped, and the build
// SUCCEEDS — only `control.txt` is written. Before this they failed CLOSED, refusing up to
// 15 corpus files outright; a refusal is still a broken lift-and-shift.
//
// BOUNDARY: this receipt proves only the context-ABSENT case. A real multibranch build
// with a CHANGE_ID, or a build with an actual changelog, is not covered — see FG-048c.
pipeline {
    agent any
    stages {
        stage('buildingTag') {
            when { buildingTag() }
            steps { sh 'echo ran > buildingTag.txt' }
        }
        stage('changeRequest') {
            when { changeRequest() }
            steps { sh 'echo ran > changeRequest.txt' }
        }
        stage('changeRequest-filtered') {
            when { changeRequest target: 'main' }
            steps { sh 'echo ran > changeRequest-filtered.txt' }
        }
        stage('changeset') {
            when { changeset '**/*.java' }
            steps { sh 'echo ran > changeset.txt' }
        }
        stage('changelog') {
            when { changelog '.*fix.*' }
            steps { sh 'echo ran > changelog.txt' }
        }
        stage('triggeredBy') {
            when { triggeredBy 'TimerTrigger' }
            steps { sh 'echo ran > triggeredBy.txt' }
        }
        stage('isRestartedRun') {
            when { isRestartedRun() }
            steps { sh 'echo ran > isRestartedRun.txt' }
        }
        stage('control') {
            steps { sh 'echo ran > control.txt' }
        }
    }
}
