// FG-187. A postfix index may not begin a new line: a statement that is complete ENDS at
// the line break, as it does in Groovy.
//
// `def picked = 'none'` followed by a line starting with `[` parsed as ONE expression —
// `'none'[…]` — so the second statement never ran and the receiver of `each` was a
// character rather than a list.
//
// TWO SHAPES, and the second is why this is P1 rather than a tidy-up:
//   - `each.txt` covers the LOUD form. Indexing a string with a list faults, so the build
//     failed where Jenkins ran both lines.
//   - `element.txt` covers the SILENT one. Where the swallowed line indexes something
//     real — a list on the previous line — the expression yields an ELEMENT instead of a
//     list and NOTHING is raised. An engine with the defect writes `first=1` here; the
//     correct answer is `first=9`, because `[9]` is a new statement's list literal.
//
// `comment.txt` covers the spelling that defeated the FIRST version of this guard, which
// walked back over spaces and tabs only. A BLOCK COMMENT between the break and the `[` got
// the old behaviour — and here that is a SILENT drop, not a loud one: the statement is
// swallowed into an index expression, its block never runs, and the build reports success.
// The guard now counts breaks inside comments too. Raised by the pre-push verifier.
//
// `same.txt` is the control that keeps the rule narrow: an index on the SAME line is
// ordinary subscripting and must still work. A guard that refused those would satisfy
// both assertions above and break every real `xs[0]` in the corpus.
//
// THIS DEFECT MASKED FG-179 FOR MONTHS. Every probe of closure capture was written across
// newlines, so `def a = false` / `[1].each { a = true }` failed for a reason that had
// nothing to do with closures — and review and I agreed on the wrong mechanism. The
// sibling cases `script-closure-mutates-ordinary` and `script-break-keeps-assignment` were
// written with explicit semicolons to dodge it; both have had them removed in the same
// commit as this case, which is the real test that this landed.
pipeline {
    agent any
    stages {
        stage('Probe') {
            steps {
                script {
                    def picked = 'none'
                    [1, 2].each { picked = 'ran' }
                    sh "printf 'each=%s' '${picked}' > each.txt"

                    def xs = [1, 2]
                    def first = 0
                    [9].each { first = it }
                    sh "printf 'first=%s' '${first}' > element.txt"

                    def viaComment = 0
                    /* a
                       comment */ [7].each { viaComment = it }
                    sh "printf 'comment=%s' '${viaComment}' > comment.txt"

                    sh "printf 'same=%s' '${xs /* still an index */ [1]}' > same.txt"
                }
            }
        }
    }
}
