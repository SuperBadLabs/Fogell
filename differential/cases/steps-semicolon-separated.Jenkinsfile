// FG-134. Semicolons separate statements in Groovy, and a `steps` block may use
// them: `steps { sh 'a'; sh 'b' }` is ordinary Declarative that Jenkins runs.
//
// The failure this pins was SILENT AND TOTAL. `many (attempt stepParser)`
// stopped at the `;`, `between` then demanded `}` and found `;`, so the whole
// step block failed — and because the `steps` section is wrapped in `attempt`,
// that failure backtracked and the section was never picked at all. `Steps`
// defaulted to [], so the stage ran NO steps, emitted nothing, and the build
// reported SUCCESS. Neither step ran and nothing said so.
//
// That is the worst outcome this engine can produce: a green build that did no
// work. It is pinned with both a same-line pair AND a trailing separator, since
// the parser now has to survive both.
// A `;` INSIDE a string literal is ordinary text, not a separator. Adding `;`
// to a character-level terminator set truncated `env.PART + '; echo b'` and
// FAILED a pipeline that passed before the FG-134 fix — a regression introduced
// while fixing the silent stage-drop, confirmed by running the same script
// against the pre-fix parser. The raw-argument scanner consumes quoted spans
// whole for that reason.
pipeline {
    agent any
    environment {
        PART = 'echo e > e.txt'
        PART2 = 'echo h > h.txt && '
    }
    stages {
        stage('one') { steps { sh 'echo a > a.txt'; sh 'echo b > b.txt' } }
        stage('two') {
            steps {
                sh 'echo c > c.txt';
                sh 'echo d > d.txt'
            }
        }
        stage('three') {
            steps {
                sh script: env.PART + '; echo f > f.txt'
            }
        }
        // An ESCAPED quote must not end the literal early. When it did, the `;`
        // after it terminated the argument and the build reported SUCCESS with
        // NO step-started record and no file — the silent no-op this ticket
        // exists to remove, reintroduced by the fix to the fix.
        stage('four') {
            steps {
                sh script: env.PART2 + "printf 'x\"; still literal' > g.txt"
            }
        }
    }
}
