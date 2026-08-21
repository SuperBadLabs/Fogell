# FG-015 trailing top-level source boundary

Reviewed base: `0039b3e980555f968e48c350650589be6ab89656`.

Four minimal cases were run directly from HeMan against the pinned Jenkins
2.568.1 oracle before implementation. Divergent cases were retained across
three attempts by the harness retry policy.

| suffix | Jenkins | Fogell before the fix |
|---|---|---|
| comments and whitespace only | success; only pipeline output | same, `PROVEN` |
| ordinary helper plus call | success; prints `tail:ok` after pipeline output | success; suffix output absent, `DIVERGED (1)` |
| default-parameter helper plus call | success; prints `tail-default:default` | success; suffix output absent, `DIVERGED (1)` |
| spread assignment | runs stage and post, prints `tail-before-spread`, then fails | succeeds after the same pipeline effects; suffix output absent, `DIVERGED (2)` |

The workspace matched in the spread case because Jenkins reaches the trailing
fault only after its Declarative stages and post block have completed. The two
difference dimensions are terminal result and output, not two durable runs.

The cause was exact: the outer `symbol "}"` consumed trailing trivia, the
pipeline parser did not require EOF, and the suffix remained unconsumed.
`Pipeline.Epilogue` now retains the exact bytes after the outer closing brace.
The same Groovy-aware Declarative grammar owns that brace, so nested braces in
script blocks, strings, slashy literals, maps, and comments cannot terminate
the capture early. `Pipeline.empty`, the parser constructor, and the public
parse entry points carry the field; no custom Pipeline serializer exists.

Execution preflight parses the retained epilogue through the bounded Groovy
parser. An empty AST (whitespace/comments) remains admitted. A definite spread
write uses the shared `unsupported_spread_assignment` refusal. Any other
nonempty AST or parse failure uses stable `unsupported_epilogue`, before
workspace preparation or effects. This ticket intentionally does not execute
new suffix semantics, so the three refused post-fix receipts are
`NOT COMPARABLE`; the comments-only control remains tier-1 `PROVEN`.

Analogous source-capture edges were audited. Preamble bytes are already bound
to the exact `preamble >>. skipToPipeline` consumption by `withSkippedString`;
script and `when` bodies use the shared Groovy-aware balanced-region scanner.
No second brace scanner or partial statement splitter was introduced.
