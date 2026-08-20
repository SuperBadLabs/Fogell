// FG-015 spread-dot boundary probe. Jenkins 2.568.1 is the semantic oracle.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def capture = { String label, Closure thunk ->
                        try {
                            def value = thunk()
                            sh "printf '%s' '${label}=OK:${value}' > ${label}.txt"
                        } catch (Throwable e) {
                            sh "printf '%s' '${label}=ERR:${e.class.name}' > ${label}.txt"
                        }
                    }

                    capture('list_present') {
                        [[name: 'a'], [name: 'b']]*.name
                    }
                    capture('list_null_element') {
                        [[name: 'a'], null, [name: 'b']]*.name
                    }
                    capture('list_missing_map') {
                        [[name: 'a'], [:], [name: null]]*.name
                    }
                    capture('list_scalar_failure') {
                        [[name: 'a'], 42, [name: 'b']]*.name
                    }
                    capture('null_receiver') {
                        def rows = null
                        rows*.name
                    }
                    capture('nested_projection') {
                        def groups = [[child: [name: 'a']], [child: null], [child: [name: 'b']]]
                        groups*.child*.name
                    }
                    capture('map_receiver') {
                        def values = [left: 1, right: 2]
                        values*.key
                    }
                    capture('string_receiver') {
                        'ab'*.length
                    }
                    capture('safe_after_spread') {
                        def rows = [[child: [name: 'a']], [child: null], [child: [name: 'b']]]
                        rows*.child?.name
                    }
                }
            }
        }
    }
}
