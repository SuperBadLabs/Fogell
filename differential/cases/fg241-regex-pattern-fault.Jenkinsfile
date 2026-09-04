// FG-241. A pattern that does not compile is a catchable PatternSyntaxException
// on Jenkins — intercepted by `catch (Exception)`, `catch
// (IllegalArgumentException)` and its own class, not by `catch
// (ArithmeticException)`. Until FG-241 this engine read every such pattern as
// `false`. The exception's message text is Jenkins' own and is deliberately not
// printed: the receipt seals which clause caught it. The UNCAUGHT shape (a
// `when { expression }` that fails the build) is measured on the ticket and
// not sealed here: Jenkins narrates it as a remoting-wrapped, multi-line
// exception the comparator does not yet recognise (FG-243).
pipeline {
    agent any
    stages {
        stage('catch') {
            steps {
                script {
                    def events = []
                    try {
                        def r = ('ab' ==~ /a)b|ab/)
                        events << "match-unexpected:${r}"
                    } catch (Exception e) {
                        events << 'exception-caught'
                    }
                    try {
                        def r = ('ab' =~ /a)b|ab/)
                        events << "find-unexpected:${r}"
                    } catch (IllegalArgumentException e) {
                        events << 'iae-caught'
                    }
                    try {
                        try {
                            def r = ('ab' ==~ /a)b|ab/)
                            events << "arith-unexpected:${r}"
                        } catch (ArithmeticException e) {
                            events << 'arith-overcaught'
                        }
                    } catch (Throwable t) {
                        events << 'arith-escaped'
                    }
                    try {
                        def r = ('ab' ==~ /[z-a]/)
                        events << "range-unexpected:${r}"
                    } catch (Exception e) {
                        events << 'range-caught'
                    }
                    events << "valid:${'ab' ==~ /a.|zz/}"
                    echo "order:${events}"
                }
            }
        }
    }
}
