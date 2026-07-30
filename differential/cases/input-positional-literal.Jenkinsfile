// FG-046 review fix, PR #17 round 4. A SINGLE-quoted prompt is literal on Jenkins whether
// it is written positionally or as `message:`. Quote provenance was tracked for named
// arguments only, so every positional prompt was interpolated — this one would have
// printed "Deploy to production?" where Jenkins prints "Deploy to ${TARGET}?".
pipeline {
    agent any
    environment {
        TARGET = 'production'
    }
    stages {
        stage('Gate') {
            steps {
                timeout(time: 4, unit: 'SECONDS') {
                    input 'Deploy to ${TARGET}?'
                }
            }
        }
    }
}
