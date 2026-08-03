// FG-053. The COMPACT form on one line. A line-anchored regex in the harness
// missed this, so Jenkins was told the script had no timestamps() while Fogell
// was told it had — Jenkins kept its prefixes, Fogell stripped its own, and the
// two engines were compared under different rules. The flag now comes from the
// parser, which answers this the same way for both.
pipeline {
    agent any
    options { timestamps() }
    stages {
        stage('one') { steps { sh 'echo compact > compact.txt' } }
    }
}
