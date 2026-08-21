pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def capture = { label, action ->
                        try {
                            echo "${label}:${action()}"
                        } catch (Throwable e) {
                            echo "${label}:fault:${e.class.name}"
                        }
                    }

                    def eachMutation = { slot ->
                        def xs = [1, 2, 3]
                        def seen = []
                        xs.each { v ->
                            seen << v
                            if (v == 1 && slot == 'current') { xs[0] = 8 }
                            if (v == 1 && slot == 'unvisited') { xs[1] = 9 }
                            if (v == 2 && slot == 'visited') { xs[0] = 7 }
                        }
                        "seen=${seen};list=${xs}"
                    }

                    def collectMutation = { slot ->
                        def xs = [1, 2, 3]
                        def result = xs.collect { v ->
                            if (v == 1 && slot == 'current') { xs[0] = 8 }
                            if (v == 1 && slot == 'unvisited') { xs[1] = 9 }
                            if (v == 2 && slot == 'visited') { xs[0] = 7 }
                            v * 10
                        }
                        "result=${result};list=${xs}"
                    }

                    def findAllMutation = { slot ->
                        def xs = [1, 2, 3]
                        def result = xs.findAll { v ->
                            if (v == 1 && slot == 'current') { xs[0] = 8 }
                            if (v == 1 && slot == 'unvisited') { xs[1] = 9 }
                            if (v == 2 && slot == 'visited') { xs[0] = 7 }
                            v > 1
                        }
                        "result=${result};list=${xs}"
                    }

                    def forMutation = { slot ->
                        def xs = [1, 2, 3]
                        def seen = []
                        for (v in xs) {
                            seen << v
                            if (v == 1 && slot == 'current') { xs[0] = 8 }
                            if (v == 1 && slot == 'unvisited') { xs[1] = 9 }
                            if (v == 2 && slot == 'visited') { xs[0] = 7 }
                        }
                        "seen=${seen};list=${xs}"
                    }

                    ['current', 'unvisited', 'visited'].each { slot ->
                        capture("each-${slot}") { eachMutation(slot) }
                        capture("collect-${slot}") { collectMutation(slot) }
                        capture("findall-${slot}") { findAllMutation(slot) }
                        capture("for-${slot}") { forMutation(slot) }
                    }

                    def structural = { kind, mode ->
                        def xs = [1, 2, 3]
                        def seen = []
                        def body = { v ->
                            seen << v
                            if (v == 1 && kind == 'append') { xs << 4 }
                            if (v == 1 && kind == 'remove') { xs.remove(2) }
                            v
                        }
                        def result
                        if (mode == 'each') { xs.each(body); result = seen }
                        if (mode == 'collect') { result = xs.collect(body) }
                        if (mode == 'findall') { result = xs.findAll { v -> body(v); true } }
                        if (mode == 'for') { for (v in xs) { body(v) }; result = seen }
                        "result=${result};list=${xs}"
                    }

                    ['each', 'collect', 'findall', 'for'].each { mode ->
                        capture("${mode}-append") { structural('append', mode) }
                        capture("${mode}-remove") { structural('remove', mode) }
                    }

                    def extend = [1, 2]
                    capture('for-index-extension') {
                        def seen = []
                        for (v in extend) {
                            seen << v
                            if (v == 1) { extend[3] = 4 }
                        }
                        "seen=${seen};list=${extend}"
                    }
                }
            }
        }
    }
}
