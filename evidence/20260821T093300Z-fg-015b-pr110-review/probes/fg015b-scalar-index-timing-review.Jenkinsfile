pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def capture = { label, action ->
                        def events = []
                        try {
                            action(events)
                            events << 'unexpected-success'
                        } catch (Throwable e) {
                            events << "caught:${e.class.name}"
                        }
                        echo "${label}:${events}"
                    }

                    capture('string-plain') { events ->
                        def receiver = { events << 'receiver'; 'ab' }
                        def index = { events << 'index'; 0 }
                        def rhs = { events << 'rhs'; 'X' }
                        receiver()[index()] = rhs()
                    }
                    capture('string-compound-plus') { events ->
                        def receiver = { events << 'receiver'; 'ab' }
                        def index = { events << 'index'; 0 }
                        def rhs = { events << 'rhs'; 'X' }
                        receiver()[index()] += rhs()
                    }
                    capture('string-compound-minus') { events ->
                        def receiver = { events << 'receiver'; 'ab' }
                        def index = { events << 'index'; 0 }
                        def rhs = { events << 'rhs'; 'a' }
                        receiver()[index()] -= rhs()
                    }
                    capture('string-postfix-inc') { events ->
                        def receiver = { events << 'receiver'; 'ab' }
                        def index = { events << 'index'; 0 }
                        receiver()[index()]++
                    }
                    capture('string-postfix-dec') { events ->
                        def receiver = { events << 'receiver'; 'ab' }
                        def index = { events << 'index'; 0 }
                        receiver()[index()]--
                    }
                    capture('string-oob-compound') { events ->
                        def receiver = { events << 'receiver'; 'ab' }
                        def index = { events << 'index'; 9 }
                        def rhs = { events << 'rhs'; 'X' }
                        receiver()[index()] += rhs()
                    }
                    capture('string-key-compound') { events ->
                        def receiver = { events << 'receiver'; 'ab' }
                        def index = { events << 'index'; 'zero' }
                        def rhs = { events << 'rhs'; 'X' }
                        receiver()[index()] += rhs()
                    }
                    capture('integer-plain') { events ->
                        def receiver = { events << 'receiver'; 7 }
                        def index = { events << 'index'; 0 }
                        def rhs = { events << 'rhs'; 2 }
                        receiver()[index()] = rhs()
                    }
                    capture('integer-compound') { events ->
                        def receiver = { events << 'receiver'; 7 }
                        def index = { events << 'index'; 0 }
                        def rhs = { events << 'rhs'; 2 }
                        receiver()[index()] += rhs()
                    }
                    capture('boolean-compound') { events ->
                        def receiver = { events << 'receiver'; true }
                        def index = { events << 'index'; 0 }
                        def rhs = { events << 'rhs'; 2 }
                        receiver()[index()] += rhs()
                    }
                    capture('null-compound') { events ->
                        def receiver = { events << 'receiver'; null }
                        def index = { events << 'index'; 0 }
                        def rhs = { events << 'rhs'; 2 }
                        receiver()[index()] += rhs()
                    }
                    capture('list-string-key-compound') { events ->
                        def receiver = { events << 'receiver'; [1] }
                        def index = { events << 'index'; 'zero' }
                        def rhs = { events << 'rhs'; 2 }
                        receiver()[index()] += rhs()
                    }
                    def map = [slot: 1]
                    def mapEvents = []
                    def mapReceiver = { mapEvents << 'receiver'; map }
                    def mapIndex = { mapEvents << 'index'; 'slot' }
                    def mapRhs = { mapEvents << 'rhs'; 2 }
                    mapReceiver()[mapIndex()] += mapRhs()
                    echo "map-compound:${mapEvents}:${map}"
                }
            }
        }
    }
}
