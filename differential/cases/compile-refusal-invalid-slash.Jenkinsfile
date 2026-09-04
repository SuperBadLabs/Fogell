// FG-248. `\/` is the spelling a reader would most expect to be valid — it is
// the slashy delimiter escape — and Jenkins 2.568.1 refuses it in every QUOTED
// form (`unexpected char: '\'`); only a slashy string escapes its delimiter.
// Fogell's catch-all decoded it to `/` and ran the command. This spelling is a
// direct Declarative triple-single-quoted step. Compiler wording is outside
// the compatibility claim.
pipeline {
    agent any
    stages {
        stage('must-not-run') {
            steps {
                sh '''printf "[\/]\n"'''
            }
        }
    }
}
