// FG-248, closing FG-126's named remainder. `\9`, like FG-126a's `\8`, is
// neither an octal digit nor a defined letter, and Jenkins 2.568.1 refuses the
// script at compile time. This spelling is a direct Declarative step in the
// double-quoted form, so the Declarative lexer — not the scripted parser —
// owns the refusal. Compiler wording is outside the compatibility claim.
pipeline {
    agent any
    stages {
        stage('must-not-run') {
            steps {
                sh "printf '[\9]\n'"
            }
        }
    }
}
