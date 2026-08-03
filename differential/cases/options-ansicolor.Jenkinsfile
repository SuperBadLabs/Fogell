// FG-053. `ansiColor('xterm')` — 4 corpus files, all with that one argument.
//
// DELIBERATELY EMITS NO COLOUR. The first version of this case ran
// `printf "\033[31mred\033[0m"` and diverged on the XTRACE line, not the output:
// Jenkins traced `+ printf red` (real ESC bytes, stripped by normalisation)
// while Fogell traced `+ printf 033[31mred033[0m` (the escape passed through as
// literal characters). That is a `sh` escape-handling difference and has nothing
// to do with ansiColor — the case was testing two things and reporting one.
// FG-122 carries it.
//
// What THIS case asks is the only question the option raises: does declaring
// ansiColor change anything a receipt can see, given `Trace.normaliseLine`
// genuinely strips ANSI escapes?
pipeline {
    agent any
    options {
        ansiColor('xterm')
    }
    stages {
        stage('one') {
            steps {
                sh 'echo plain > plain.txt'
                echo 'a narrated line'
            }
        }
    }
}
