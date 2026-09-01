// FG-228 companion probe: the same four link shapes with Ant default excludes
// explicitly disabled. This keeps link handling and default-exclude policy from
// being inferred from one another.
pipeline {
    agent any
    stages {
        stage('Save links') {
            steps {
                sh '''set -eu
                    rm -rf ../fg228-outside-file.txt ../fg228-outside-dir
                    mkdir -p hidden/in-dir-target ../fg228-outside-dir
                    printf ordinary > ordinary.txt
                    printf inside-file > hidden/in-file-target.txt
                    printf inside-dir > hidden/in-dir-target/value.txt
                    printf outside-file > ../fg228-outside-file.txt
                    printf outside-dir > ../fg228-outside-dir/value.txt
                    ln -s hidden/in-file-target.txt in-file-link
                    ln -s hidden/in-dir-target in-dir-link
                    ln -s ../fg228-outside-file.txt out-file-link
                    ln -s ../fg228-outside-dir out-dir-link
                '''
                stash name: 'links', includes: 'ordinary.txt,in-file-link,in-dir-link/**,out-file-link,out-dir-link/**', useDefaultExcludes: false
            }
        }
        stage('Restore links') {
            steps {
                deleteDir()
                unstash 'links'
                sh '''set -eu
                    for path in in-file-link in-dir-link out-file-link out-dir-link; do
                        if test -L "$path"; then
                            printf '%s=link:' "$path"
                            readlink "$path"
                        elif test -d "$path"; then
                            printf '%s=directory\n' "$path"
                        elif test -f "$path"; then
                            printf '%s=regular-file\n' "$path"
                        else
                            printf '%s=missing\n' "$path"
                        fi
                    done > symlink-types.txt
                    for path in ordinary.txt in-file-link in-dir-link/value.txt out-file-link out-dir-link/value.txt; do
                        if test -f "$path"; then
                            printf '%s=' "$path"
                            cat "$path"
                            printf '\n'
                        else
                            printf '%s=missing\n' "$path"
                        fi
                    done > symlink-observation.txt
                    rm -rf ../fg228-outside-file.txt ../fg228-outside-dir
                '''
            }
        }
    }
}
