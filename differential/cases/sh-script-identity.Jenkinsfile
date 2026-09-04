// FG-245. What a shell step's `$0` IS on both engines. durable-task writes
// `<workspace>@tmp/durable-<8hex>/script.sh`, copies it to `script.sh.copy`
// (JENKINS-70874: a writable handle to the original, inherited by a fork,
// raised "Text file busy") and executes the COPY, so `$0` ends in
// `script.sh.copy`, the original stays beside it, and the copy carries the
// original's mode: a plain script is not executable, a shebang script is.
// Fogell ran `script.sh` itself until this case; the first corpus file whose
// output names `$0` (dash's `not found` line in
// `linuxacademy_cicd-pipeline-train-schedule-cd`) diverged on exactly that
// basename. The durable id is normalised to `<id>` on both sides; the
// basename, the execute bit and the original's presence are compared.
pipeline {
    agent any
    stages {
        stage('identity') {
            steps {
                sh 'echo "$0"; if [ -x "$0" ]; then echo exec; else echo noexec; fi; if [ -f "${0%.copy}" ]; then echo original-present; else echo original-absent; fi'
                sh '''#!/bin/bash
echo "$0"; if [ -x "$0" ]; then echo exec; else echo noexec; fi'''
            }
        }
    }
}
