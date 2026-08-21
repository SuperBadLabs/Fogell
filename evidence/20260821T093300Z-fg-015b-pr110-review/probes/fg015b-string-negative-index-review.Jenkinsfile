pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def capture = { label, indexValue ->
                        def events = []
                        try {
                            def receiver = { events << 'receiver'; 'ab' }
                            def index = { events << 'index'; indexValue }
                            def rhs = { events << 'rhs'; 'X' }
                            receiver()[index()] += rhs()
                            events << 'unexpected-success'
                        } catch (Throwable e) {
                            events << "caught:${e.class.name}"
                        }
                        echo "${label}:${events}"
                    }

                    capture('string-minus-one', -1)
                    capture('string-too-negative', -3)
                }
            }
        }
    }
}
