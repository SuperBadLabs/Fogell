// FG-193. A Groovy map is a REFERENCE object: a mutation through one alias is
// visible to every name holding the same map. MEASURED before the fix as
// jenkins=alias:x fogell=alias:null — the write vanished into a dropped match
// arm, a wrong value under a green build. VMap carries a ref now, the identity
// aliases share, exactly as ref cells are the identity locals share (FG-179).
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def local = [:]
                    def other = local
                    other.FOO = 'x'
                    echo "alias:${local.FOO}"
                }
            }
        }
    }
}
