// FG-159. A CONCURRENT case where both engines print the SAME inherited-looking
// literal — so the fold must NOT be credited for it.
//
// `/var/jenkins_home` is Jenkins' HOME value, so canonicalisation rewrites it to
// `${HOME}` on BOTH sides. But both engines print the identical literal here, so the
// rows compare byte-equal and the relaxation decides nothing. The ordered path gets
// this right for free — `jh = fh` wins before the canonical branch is reached — and
// the concurrent path filtered on `canon l <> l` alone, which credited the fold for
// any row merely CONTAINING an inherited value.
//
// This case exists because the fix for that over-reporting was otherwise UNEXERCISED,
// which is the FG-158 trap verbatim: a fix that cannot fire is indistinguishable from
// one that works. Under the old filter these rows appear in the receipt's notes; under
// the multiset-difference filter they do not.
//
// The literal is written out rather than expanded on purpose: `echo $HOME` would print
// each engine's OWN home and genuinely need the fold, which is the other case
// (`parallel-inherited-env-fold`). Here both sides must emit the same bytes.
pipeline {
    agent any
    stages {
        stage('fan') {
            parallel {
                stage('left') {
                    steps {
                        sh 'echo /var/jenkins_home'
                    }
                }
                stage('right') {
                    steps {
                        sh 'echo /var/jenkins_home'
                    }
                }
            }
        }
    }
}
