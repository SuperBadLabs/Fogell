// FG-248. The escape LETTERS of a quoted Groovy string, on both parsers. FG-124a
// sealed the numeric grammar for the scripted parser; this case seals the nine
// letters Jenkins 2.568.1 accepts after a backslash — `b f n t r \ ' " $` —
// in all four quoted forms, first inside `script { }` (the scripted parser)
// and then as direct Declarative steps (the Declarative lexer). Every decoded
// value is reduced to exact hex bytes in the workspace, so a letter mapped to
// the wrong control character, or a backslash silently dropped, changes the
// workspace hash even where console normalisation would hide it.
//
// Each form splits the nine letters over two `printf` calls because no single
// POSIX quoting style can carry `'`, `"`, `\` and `$` at once: the first call
// hands `[\b\f\n\t\r']` to the shell inside double quotes, the second hands
// `["\$]` inside single quotes. Each call writes a file and one `od` dumps
// both on a single line, so the expected line is
// `5b 08 0c 0a 09 0d 27 5d 5b 22 5c 24 5d` in every quoted form. No step
// uses a shell pipeline: `sh -x` traces each pipeline stage from its own
// process, and the first sealing of this case with `printf | od | tr` drew a
// `+ + printf` interleaving on the Jenkins side (the FG-119 race) that the
// comparator does not recover for this shape.
//
// The slashy control is deliberately different and stays that way: a slashy
// string escapes only its delimiter, so `\b\f\n\t\r\s\q` reach the shell as
// literal backslash pairs and `\/` as `/`. The measured-invalid spellings —
// `\/`, `\s`, `\q` and the rest — are compile refusals in every quoted form and
// have their own `compile-refusal-invalid-*` cases; they cannot share a file
// with an executed step, because the refusal stops the whole build.
pipeline {
    agent any
    stages {
        stage('Scripted letters') {
            steps {
                script {
                    sh 'printf "%s" "[\b\f\n\t\r\']" > s-single-a.bin; printf "%s" \'[\"\\\$]\' > s-single-b.bin; od -An -tx1 -w32 s-single-a.bin s-single-b.bin > s-single.hex; cat s-single.hex'
                    sh '''printf "%s" "[\b\f\n\t\r\']" > s-triple-single-a.bin; printf "%s" \'[\"\\\$]\' > s-triple-single-b.bin; od -An -tx1 -w32 s-triple-single-a.bin s-triple-single-b.bin > s-triple-single.hex; cat s-triple-single.hex'''
                    sh "printf \"%s\" \"[\b\f\n\t\r\']\" > s-double-a.bin; printf \"%s\" '[\"\\\$]' > s-double-b.bin; od -An -tx1 -w32 s-double-a.bin s-double-b.bin > s-double.hex; cat s-double.hex"
                    sh """printf \"%s\" \"[\b\f\n\t\r\']\" > s-triple-double-a.bin; printf \"%s\" '[\"\\\$]' > s-triple-double-b.bin; od -An -tx1 -w32 s-triple-double-a.bin s-triple-double-b.bin > s-triple-double.hex; cat s-triple-double.hex"""
                    sh(/printf '%s' '[\b\f\n\t\r\s\q\/]' > s-slashy.bin; od -An -tx1 -w32 s-slashy.bin > s-slashy.hex; cat s-slashy.hex/)
                }
            }
        }
        stage('Declarative letters') {
            steps {
                sh 'printf "%s" "[\b\f\n\t\r\']" > d-single-a.bin; printf "%s" \'[\"\\\$]\' > d-single-b.bin; od -An -tx1 -w32 d-single-a.bin d-single-b.bin > d-single.hex; cat d-single.hex'
                sh '''printf "%s" "[\b\f\n\t\r\']" > d-triple-single-a.bin; printf "%s" \'[\"\\\$]\' > d-triple-single-b.bin; od -An -tx1 -w32 d-triple-single-a.bin d-triple-single-b.bin > d-triple-single.hex; cat d-triple-single.hex'''
                sh "printf \"%s\" \"[\b\f\n\t\r\']\" > d-double-a.bin; printf \"%s\" '[\"\\\$]' > d-double-b.bin; od -An -tx1 -w32 d-double-a.bin d-double-b.bin > d-double.hex; cat d-double.hex"
                sh """printf \"%s\" \"[\b\f\n\t\r\']\" > d-triple-double-a.bin; printf \"%s\" '[\"\\\$]' > d-triple-double-b.bin; od -An -tx1 -w32 d-triple-double-a.bin d-triple-double-b.bin > d-triple-double.hex; cat d-triple-double.hex"""
                sh(/printf '%s' '[\b\f\n\t\r\s\q\/]' > d-slashy.bin; od -An -tx1 -w32 d-slashy.bin > d-slashy.hex; cat d-slashy.hex/)
            }
        }
    }
}
