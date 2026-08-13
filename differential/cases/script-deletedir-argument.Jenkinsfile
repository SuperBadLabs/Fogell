// FG-177 slice 1. `deleteDir` takes NO argument, and admitting one DESTROYED WORK.
//
// The arity default this replaces allowed "zero or one positional" for any step without
// its own arm. `deleteDir('ignored')` passed it, the arm ignored the argument, and Fogell
// wiped the workspace and carried on — where Jenkins keeps the files and FAILS, because
// `DeleteDirStep` has an empty constructor and Jenkins' positional binding only applies
// to a step with a sole REQUIRED parameter.
//
// A destructive false success, and the one shape a blanket rule could not express: it
// cannot tell a step with one required parameter from a step with none. That is why the
// arity is now per-step DATA (`WalkerRules.positionalArity`) rather than a default.
//
// `keep.txt` IS THE ASSERTION and it must SURVIVE on both engines. The terminal result
// alone would pass an engine that deleted the workspace and then failed for some other
// reason — the deletion is the damage, not the verdict. `after.txt` must be absent, so
// the refusal stops the block rather than being reported and stepped over.
pipeline {
    agent any
    stages {
        stage('Prep') {
            steps { sh 'echo keep > keep.txt' }
        }
        stage('Wipe') {
            steps {
                script {
                    deleteDir('ignored')
                    sh 'echo after > after.txt'
                }
            }
        }
    }
}
