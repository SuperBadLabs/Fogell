// FG-183. `switch` is a BREAK BOUNDARY and it FALLS THROUGH — both measured against
// Jenkins, because Fogell now models the construct instead of lowering it away.
//
// THIS CASE IS THE FOURTH POSITION ON ONE QUESTION, and the first that is not a guess
// about what a lowered tree meant. `switch` was flattened to nested ifs, so every stage
// downstream had to reconstruct a boundary the parser had already discarded: the arm-final
// `break` escaped as a signal; consuming it left the others, which INSIDE A LOOP reached
// the loop handler and silently left the LOOP; refusing those over-refused
// `case 'a': if (true) break`, which Groovy accepts. Three defensible positions, three
// defects. `SSwitch` supplies the structure and the questions stop.
//
// THE LOOP IS ESSENTIAL, not decoration. A `break` in an arm is indistinguishable from a
// loop `break` unless something owns the boundary, and the failure it pins was visible
// ONLY inside a loop — outside one the shape was refused, so the earlier fix looked
// complete. `log` accumulating `a` after the switch is what proves the loop kept going.
//
// WHAT EACH LETTER PROVES, in order:
//   - `a` alone from the first iteration: `if (true) break` left the SWITCH, not the loop,
//     and did not run the rest of its arm. An engine that leaves the loop stops at `a`.
//   - `B` then `b`: an ordinary arm-final `break`, still working.
//   - `D` then `z`: no case matched, so `default` ran — by POSITION, since fallthrough
//     makes where it sits matter, not merely that it exists.
//   - `PQ` from the second switch: an arm with NO `break` FALLS INTO THE NEXT. The old
//     lowering could not express this at all — nested ifs are mutually exclusive — and it
//     was recorded as a known gap justified by the corpus rather than by the language.
//     Modelling it and not testing it would be the same overclaim in a new place.
pipeline {
    agent any
    stages {
        stage('Switch') {
            steps {
                script {
                    def log = ''

                    for (i in ['a', 'b', 'z']) {
                        switch (i) {
                            case 'a':
                                if (true) {
                                    break
                                }
                                log = log + '!'
                            case 'b':
                                log = log + 'B'
                                break
                            default:
                                log = log + 'D'
                        }

                        log = log + i
                    }

                    def fall = ''

                    switch ('p') {
                        case 'p':
                            fall = fall + 'P'
                        case 'q':
                            fall = fall + 'Q'
                            break
                        default:
                            fall = fall + 'R'
                    }

                    echo "log:[${log}] fall:[${fall}]"
                    sh "printf 'log=%s fall=%s' '${log}' '${fall}' > switch.txt"
                }
            }
        }
    }
}
