# FG-180 + FG-141 pre-fix parser probes — 2026-08-18

Parse-only measurement of the Groovy grammar against the pinned corpus's 59
`malformed_syntax` rejects. Corpus code was never executed. Oracle:
`Fogell.Groovy.Parser.Parser.parse` from the Release build at main+FG-114.

## Method, and why three instruments

1. `fg180-classify.py` — pull each ledger row's error position and print the
   source line under it. **Result: positions are FParsec outermost-choice
   artifacts** — a dozen files "fail" at `node {` column 6 while the real
   defect is inside the closure body. Reading constructs off error positions
   repeats the by-eye mistake the FG-180 row documents; kept as the map of
   which files to probe, nothing else.
2. `fg180-firstbad.fsx` — longest prefix (auto-closed braces) that parses;
   the first non-extendable line names the construct. Caveat measured on
   itself: a prefix cut mid block-comment / mid multiline-list fails for the
   cut, not the grammar — those hits were re-checked in isolation.
   (`fg180-reduce.fsx`, plain line-ddmin, degenerated to a bare `}` and was
   discarded — kept out of the dir on purpose; the caveat lives here.)
3. `fg180-probe3.fsx` — each candidate construct isolated, balanced,
   verdict-only. This list is the finding.

## Real grammar gaps (isolated, measured)

| construct | example | files hit |
|---|---|---|
| command-form call in expression position, NAMED | `def n = tool name: 'x', type: 'y'` | captjt, mraible, Jotschi (+ script blocks) |
| command-form call inside GString placeholder | `"${tool 'M3'}/bin"` | arun-gupta, ricardozanini, kishorebhatia |
| command-form call in expression position, POSITIONAL | `def m = tool 'M3'` | **SILENT MISPARSE**, see below |
| typed / default-param function decl (even `def f(String s)`) | `void f(Maven m) {}` | alexguzun, cloudogu ×2, j8kin, judexzhu, jenkinsci_docker-agents |
| string-named command args | `parallel 'a': { }, 'b': { }` | esign, jalogut, kesselborn, camiloribeiro |
| closure literal as assignment RHS | `builds['a'] = { }` | jenkinsci_jenkins |
| `.new(...)` reserved word as member | `lib.Wrapper.new(this, 'x')` | cloudogu_ces-build-lib |
| `in` operator | `b in ['x','y']` | microsoft_movie-db |
| GString property name | `m."$name" = ''` | gdemengin |
| C-style `for` | `for (int i = 0; i < 3; ++i)` | jenkinsci_jenkinsfile-runner |

## The silent misparse (worse than a reject)

`def m = tool 'Maven 3.3.9'` **admits** as
`SDef("m", Some(EVar "tool")); SExpr(EStr "Maven 3.3.9")` — two statements,
`m` bound to an unresolved variable, the argument a no-op. Same shape for
plain assignment: `mvnHome = tool 'M3'` → `SAssign(…, EVar "tool")`. This
settles the FG-180 row's OPEN QUESTION in the bad direction: the positional
form is "an admission that is honest about nothing". Any command-form fix
must cover expression position for BOTH forms, or file counts will improve
while wrong ASTs keep admitting.

## Cleared as artifacts (parse OK in isolation)

Trailing closures (`node { }`, `node('x') { }`, dotted), untyped `def f(a)`
decls, multiline list literals, multiline call parens, block comments,
try/catch (all newline shapes), trailing-`+` continuation, ternary, elvis,
safe navigation, `$class` map keys, `@Field` annotation, imports.

## FG-141 scope narrowing (measured in probe1)

`def p = /a}b/` → `EStr "a}b"` and `def x = 10 / 2` → `EBinary` — the
Groovy expression grammar already resolves slashy-vs-division by position.
FG-141's defect is confined to the two pre-scanners (raw-argument scanner,
`balancedBody`), which lack exactly the context the grammar has.

## Not probed here

The five FG-180 script-block files' inner constructs (Romeh ×2 inner 7:21,
MrRameshRajendran, Terradue, varunpalekar `no line break before '['`) — the
scorecard after each fix slice is the measurement that matters; per-file
diagnosis follows the first slice. Declarative-path rejects (`tools`,
`parameters`, `environment`, `stages` opaque, ~24 files) are DIFFERENT
tickets — not Groovy grammar.
