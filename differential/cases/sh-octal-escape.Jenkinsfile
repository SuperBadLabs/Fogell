// FG-122. A single-quoted `sh` body carrying a backslash escape.
//
// MEASURED before the fix: Jenkins traced `+ printf red` — a real ESC byte,
// which normalisation strips — and Fogell traced `+ printf 033[31mred033[0m`,
// because the Groovy string lexer mapped only \n, \t and \r and returned the
// escape's first character for everything else. The two engines ran DIFFERENT
// COMMANDS for the same Jenkinsfile.
//
// Found while writing the FG-053 ansiColor case, which conflated this with the
// option under test and reported one of the two.
pipeline {
    agent any
    stages {
        stage('one') {
            steps {
                sh 'printf "\033[31mred\033[0m\n" > colour.txt; cat colour.txt'
                sh 'printf "tab\there\n"'
            }
        }
    }
}
