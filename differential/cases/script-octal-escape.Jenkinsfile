// FG-124a. The scripted-Groovy counterpart to FG-122's Declarative escape
// receipt. Every quoted form below carries the same simple, Unicode and octal
// inventory, and every decoded value is reduced to exact hex bytes in the
// workspace. That makes digit-boundary mistakes visible even when console
// normalisation would hide a control character.
//
// Octal takes one or two digits, or three only when the first is 0-3. Thus
// `\377` is U+00ff, `\400` is SPACE plus literal `0`, and `\777` is `?7`.
// Repeated Unicode `u` characters are accepted. The slashy control is
// deliberately different: it retains simple and octal-looking backslash
// sequences literally and is not routed through this quoted-string decoder.
pipeline {
    agent any
    stages {
        stage('Scripted escapes') {
            steps {
                script {
                    sh 'printf \'%s\' \'\b\f\u0041\uu0042\7\77\377\400\777\' | od -An -tx1 | tr -d \' \' > single.hex; printf \'single=\'; cat single.hex'
                    sh '''printf '%s' '\b\f\u0041\uu0042\7\77\377\400\777' | od -An -tx1 | tr -d ' ' > triple-single.hex; printf 'triple-single='; cat triple-single.hex'''
                    sh "printf '%s' '\b\f\u0041\uu0042\7\77\377\400\777' | od -An -tx1 | tr -d ' ' > double.hex; printf 'double='; cat double.hex"
                    sh """printf '%s' '\b\f\u0041\uu0042\7\77\377\400\777' | od -An -tx1 | tr -d ' ' > triple-double.hex; printf 'triple-double='; cat triple-double.hex"""
                    sh(/printf '%s' '\b\f\7\77\377\400\777' | od -An -tx1 | tr -d ' ' > slashy.hex; printf 'slashy='; cat slashy.hex/)
                }
            }
        }
    }
}
