pipeline {
    agent any
    options { skipDefaultCheckout(true) }
    stages {
        stage('probe') {
            steps {
                sh "printf schema > fg177-archive-schema.txt"
                script {
                    def value = archiveArtifacts(
                        artifacts: 'fg177-archive-schema.txt',
                        allowEmptyArchive: false,
                        caseSensitive: true,
                        defaultExcludes: true,
                        excludes: 'never/**',
                        fingerprint: true,
                        followSymlinks: true,
                        onlyIfSuccessful: false
                    )
                    echo "FG177 ARCHIVE SUPPORTED-KEYS CONTINUED CLASS=${value == null ? 'null' : value.getClass().name} VALUE=${value}"
                }
            }
        }
    }
}
