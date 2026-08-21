pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    try {
                        def a = [null]
                        def b = [null]
                        a[0] = a
                        b[0] = b
                        [a, b].sort()
                        echo 'cycle-exception:unexpected-success'
                    } catch (Exception e) {
                        echo 'cycle-exception:caught-exception'
                    } catch (Throwable e) {
                        echo "cycle-exception:caught-${e.class.name}"
                    }

                    try {
                        def a = [null]
                        def b = [null]
                        a[0] = a
                        b[0] = b
                        [a, b].sort()
                        echo 'cycle-error:unexpected-success'
                    } catch (Error e) {
                        echo "cycle-error:caught-${e.class.name}"
                    }

                    try {
                        def s = 'ab'
                        s[0] += 'X'
                        echo 'scalar-security:unexpected-success'
                    } catch (SecurityException e) {
                        echo "scalar-security:caught-${e.class.name}"
                    } catch (Throwable e) {
                        echo "scalar-security:wrong-${e.class.name}"
                    }

                    try {
                        def s = 'ab'
                        s[9] += 'X'
                        echo 'string-oob-index:unexpected-success'
                    } catch (IndexOutOfBoundsException e) {
                        echo "string-oob-index:caught-${e.class.name}"
                    } catch (Throwable e) {
                        echo "string-oob-index:wrong-${e.class.name}"
                    }

                    try {
                        def s = 'ab'
                        s[9] += 'X'
                        echo 'string-oob-array:unexpected-success'
                    } catch (ArrayIndexOutOfBoundsException e) {
                        echo "string-oob-array:wrong-${e.class.name}"
                    } catch (Throwable e) {
                        echo "string-oob-array:escaped-${e.class.name}"
                    }
                }
            }
        }
    }
}
