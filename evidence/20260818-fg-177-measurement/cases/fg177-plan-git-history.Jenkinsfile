// FG-177 RETAINED-HISTORY MEASUREMENT PLAN, not archived evidence.
// The renderer injects FOGELL_SCM_URL, but run-probes deliberately does not run
// this plan automatically. Execute its rendered file through the differential
// CLI's runMany lane only when both build receipts will be retained and reviewed.
pipeline {
    agent any
    stages {
        stage('build-1') {
            steps {
                script {
                    dir('fg177-history-git') {
                        def value = git(url: @@FOGELL_SCM_URL@@, branch: @@FOGELL_GIT_PINNED_BRANCH@@)
                        echo "FG177 HISTORY git BUILD=1 CLASS=${value == null ? 'null' : value.getClass().getName()} VALUE=${value}"
                        if (value instanceof Map) {
                            echo "FG177 HISTORY git BUILD=1 KEYS=${value.keySet().sort().join(',')}"
                            value.keySet().sort().each { k -> echo "FG177 HISTORY git BUILD=1 ENTRY ${k} CLASS=${value[k] == null ? 'null' : value[k].getClass().getName()} VALUE=${value[k]}" }
                        }
                    }
                }
            }
        }
    }
}
//// NEXT BUILD ////
pipeline {
    agent any
    stages {
        stage('build-2') {
            steps {
                script {
                    dir('fg177-history-git') {
                        def value = git(url: @@FOGELL_SCM_URL@@, branch: @@FOGELL_GIT_PINNED_BRANCH@@)
                        echo "FG177 HISTORY git BUILD=2 CLASS=${value == null ? 'null' : value.getClass().getName()} VALUE=${value}"
                        if (value instanceof Map) {
                            echo "FG177 HISTORY git BUILD=2 KEYS=${value.keySet().sort().join(',')}"
                            value.keySet().sort().each { k -> echo "FG177 HISTORY git BUILD=2 ENTRY ${k} CLASS=${value[k] == null ? 'null' : value[k].getClass().getName()} VALUE=${value[k]}" }
                        }
                        echo "FG177 HISTORY git BUILD=2 PREVIOUS_COMMIT=${value instanceof Map ? value['GIT_PREVIOUS_COMMIT'] : '<not-map>'}"
                        echo "FG177 HISTORY git BUILD=2 PREVIOUS_SUCCESSFUL_COMMIT=${value instanceof Map ? value['GIT_PREVIOUS_SUCCESSFUL_COMMIT'] : '<not-map>'}"
                    }
                }
            }
        }
    }
}
