// FG-046 review fix, PR #17 round 8. A slashy string inside a GString placeholder:
// `${/}/}` holds the literal `}`, which is CONTENT, not the placeholder's end. The
// balanced scanner tracked only `'` and `"`, so it disagreed with `Fogell.Groovy.Parser`,
// which already accepts slashy strings — the scanner and the parser that consumes its
// output must recognise the same literal forms.
pipeline {
    agent any
    stages {
        stage('Gate') {
            steps {
                timeout(time: 4, unit: 'SECONDS') {
                    input message: "Result: ${/}/}"
                }
            }
        }
    }
}
