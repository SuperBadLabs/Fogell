// FG-046 review fix, PR #17 round 9. `/` is both a slashy-string delimiter and DIVISION.
// Round 8 taught the scanner that `/` opens a literal, which then broke `${10 / 2}`.
// Groovy disambiguates by what precedes it: after a value a slash divides, otherwise it
// opens a literal. Both forms appear here so neither fix can regress the other.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                timeout(time: 4, unit: 'SECONDS') {
                    input message: "Half of ten is ${10 / 2}, and a brace is ${/}/}"
                }
            }
        }
    }
}
