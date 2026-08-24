// FG-203. Jenkins' `env` is one live object. Aliases captured before and inside
// withEnv observe nested overlays, then both normal and exceptional restoration.
// The final branch makes the restoration check non-vacuous in the workspace.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    def before = env
                    def inside
                    echo "before:${before.FG203_ALIAS_SCOPE}:${before.FG203_ALIAS_INNER}:${env.FG203_ALIAS_SCOPE}"
                    withEnv(['FG203_ALIAS_SCOPE=outer']) {
                        inside = env
                        echo "outer:${before.FG203_ALIAS_SCOPE}:${inside.FG203_ALIAS_SCOPE}:${env.FG203_ALIAS_INNER}"
                        withEnv(['FG203_ALIAS_SCOPE=inner', 'FG203_ALIAS_INNER=yes']) {
                            echo "inner:${before.FG203_ALIAS_SCOPE}:${inside.FG203_ALIAS_SCOPE}:${env.FG203_ALIAS_INNER}"
                        }
                        echo "restored:${before.FG203_ALIAS_SCOPE}:${inside.FG203_ALIAS_SCOPE}:${env.FG203_ALIAS_INNER}"
                        try {
                            withEnv(['FG203_ALIAS_SCOPE=fault', 'FG203_ALIAS_INNER=fault']) {
                                echo "during-fault:${before.FG203_ALIAS_SCOPE}:${inside.FG203_ALIAS_SCOPE}:${env.FG203_ALIAS_INNER}"
                                def ignored = 1 / 0
                            }
                        } catch (Exception caught) { }
                        echo "after-fault:${before.FG203_ALIAS_SCOPE}:${inside.FG203_ALIAS_SCOPE}:${env.FG203_ALIAS_INNER}"
                    }
                    echo "after:${before.FG203_ALIAS_SCOPE}:${inside.FG203_ALIAS_SCOPE}:${env.FG203_ALIAS_SCOPE}"
                    if (before.FG203_ALIAS_SCOPE == null &&
                        inside.FG203_ALIAS_SCOPE == null &&
                        env.FG203_ALIAS_SCOPE == null &&
                        env.FG203_ALIAS_INNER == null) {
                        sh 'printf fresh > alias-result.txt'
                    } else {
                        sh 'printf stale > alias-result.txt'
                    }
                }
            }
        }
    }
}
