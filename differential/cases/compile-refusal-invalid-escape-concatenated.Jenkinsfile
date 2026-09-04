// FG-248, from Codex's review of PR #410. An invalid escape inside a quoted
// literal that is only PART of a step argument — here a concatenation — is
// seen by the raw-argument scanner, not by a string decoder. Jenkins 2.568.1
// refuses the whole file at compile time; before this case Fogell admitted it,
// ran the first step (a durable effect Jenkins never produces, the FG-175
// class) and failed only when the second step's expression was evaluated.
// The receipt compares the typed refusal disposition, terminal result and the
// workspace hash, which must be the empty tree on both sides: `early.txt`
// must not exist. Compiler wording is outside the compatibility claim.
pipeline {
    agent any
    stages {
        stage('must-not-run') {
            steps {
                sh 'echo early > early.txt'
                sh 'printf "%s" ' + '"[\q]"'
            }
        }
    }
}
