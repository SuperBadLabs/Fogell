// FG-194. `break` inside a `for-in` leaves the LOOP, not just the iteration.
//
// The handler caught `BreakSignal` per ITERATION and carried on with the next element, so
// the body ran for every one. `SWhile` one arm below has always stopped correctly, which
// is what makes this a transcription slip rather than a design question.
//
// `picked` is the assertion: with a working break it holds the FIRST element only. An
// engine that resumes after the break holds all three, and one that treats break as
// `continue` also holds all three — so the same marker separates both wrong answers from
// the right one.
//
// `after.txt` proves execution continued PAST the loop, which distinguishes "break left
// the loop" from "break escaped the script entirely" — the second is what an uncaught
// signal would do, and it is the shape FG-183 was about.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def picked = ''

                    for (x in ['a', 'b', 'c']) {
                        picked = picked + x
                        break
                    }

                    echo "picked:[${picked}]"
                    sh "printf 'picked=%s' '${picked}' > after.txt"
                }
            }
        }
    }
}
