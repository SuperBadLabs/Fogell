// FG-178. SIBLING WRAPPERS INSIDE A WRAPPER each get their own environment.
//
// The fourth case in this family, and it pins the failure mode of my THIRD attempt at the
// shadowing fix. That attempt anchored provenance on interpreter STATE, which is SHARED:
// the first `withEnv` refresh overwrote the marker, the second sibling's binding then
// failed the equality check, it was classified as a user-defined `env`, and its refresh
// was SKIPPED — reintroducing the exact staleness the fix exists to prevent, one level in.
//
// TWO SIBLINGS ARE THE POINT. A single wrapper passes whether provenance is per-closure,
// per-state or absent; only a SECOND one after a completed first can show a marker that
// the first invalidated. Nesting them inside `dir` also proves the outer wrapper's context
// survives both.
//
// The rule finally stopped moving when provenance became a FACT the interpreter records —
// the script declaring or assigning its own `env` — instead of an inference from comparing
// values. Nothing to be coincidentally equal to, and nothing shared that should not be.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    dir('sub') {
                        withEnv(['A=one']) {
                            sh "printf a:${env.A} > a.txt"
                        }
                        withEnv(['TARGET=prod']) {
                            sh "printf t:${env.TARGET} > t.txt"
                        }
                    }
                }
            }
        }
    }
}
