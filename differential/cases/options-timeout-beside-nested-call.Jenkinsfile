// FG-150. A pipeline `options` block holding BOTH a nested-call directive and a
// directive Fogell honours.
//
// MEASURED: `buildDiscarder(logRotator(numToKeepStr: '5'))` made the whole options
// block unparseable — `rawArgValue` stops at `)`, so the nested call left a stray
// paren and the reparse failed. The top-level fallback then recorded the block as an
// opaque section, and the `timeout` beside it was DROPPED WITHOUT A WORD: a 5-second
// step ran to `completed: success` under a 1-second timeout. Jenkins aborts.
//
// A REGRESSION INTRODUCED BY FG-147's fail-closed, proven against a control (the same
// timeout alone aborts) and against merged `fe0b095` (aborts). Fail-closed was the
// right change and its blast radius went unchecked — the third time on this branch a
// fix of mine created a defect one layer out.
//
// `options-accept-and-ignore` ALREADY CONTAINS THIS EXACT `logRotator` FORM and stayed
// PROVEN throughout, because every option in that case is one Fogell ignores anyway —
// dropping them changes nothing it compares. A case can hold the broken construct and
// still be blind to the break. This one pairs the construct with an option whose loss
// CHANGES THE TERMINAL RESULT, which is what makes it a guard rather than a sample.
pipeline {
    agent any
    options {
        buildDiscarder(logRotator(numToKeepStr: '5'))
        timeout(time: 1, unit: 'SECONDS')
    }
    stages {
        stage('one') {
            steps {
                sh 'sleep 5; echo finished > done.txt'
            }
        }
    }
}
