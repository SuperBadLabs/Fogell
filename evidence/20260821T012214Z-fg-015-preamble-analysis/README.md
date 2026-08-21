# FG-015 unanalyzable-preamble review boundary

Reviewed base: `b1cf6c3d6ef3811e1308cf97f89901ee0bb8812b`.

The minimal `default-helper-spread.Jenkinsfile` was run directly from HeMan
against the pinned Jenkins 2.568.1 oracle before implementation. Across three
retained attempts Jenkins failed before stage or post effects and left an empty
workspace. Fogell succeeded, ran `touch stage-ran.txt`, and produced a nonempty
workspace. The sealed pre-fix differential receipt is
`preimplementation-receipts/fg015-default-helper-spread.receipt.txt`.

The pipeline parser correctly captured the complete preamble. Fogell's bounded
Groovy parser rejected the Jenkins-valid default parameter in
`def helper(x = true)`, and the spread-write preflight converted that parse
error to `false`. It therefore misclassified the later top-level
`rows*.name = 'x'` as absent.

The correction makes a nonblank preamble analysis failure a stable
`unsupported_preamble_analysis` execution refusal before workspace preparation
or effects. It does not invent a partial Groovy statement splitter and does not
broaden the parser. Blank and fully analyzed preambles preserve their existing
behavior. Script-body parse errors were audited separately: they are not
discarded by the execution path, which surfaces them when the hosted block is
reached; only the preamble path intentionally maps a parse failure to an empty
function set, so that is the shared fail-open boundary closed here.

The post-fix receipt is expected to be `NOT COMPARABLE`: Jenkins reaches its
catchable spread-assignment runtime failure, while Fogell makes the documented
conservative refusal earlier. Timing and catchability parity are not claimed.
