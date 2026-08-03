// FG-053. `timestamps()` is the second-most-used option in the corpus (14 files).
// MEASURING FIRST: what the option does to the console is the whole question,
// and the receipt contract already claims "excluded: timestamps" while nothing
// in Trace.normaliseLine strips one — true today only because no case uses it.
pipeline {
    agent any
    options {
        timestamps()
    }
    stages {
        stage('one') {
            steps {
                sh 'echo first > first.txt'
                echo 'a narrated line'
            }
        }
    }
}
