# FG-177 JUnit summary-count evidence

This bundle binds one Jenkins 2.568.1 / Fogell differential run for
`fg177-junit-summary-counts.Jenkinsfile`. The case records a four-test report
containing one pass, one failure, one error, and one skip. Both engines finish
`unstable`, emit the same ordered output, and leave the same workspace marker
only when `totalCount == 4`, `failCount == 2`, and `skipCount == 1`.

The oracle is checked before and after the run against the retained FG-177 pin:
154 plugins, the Jenkins session identity, the immutable container identity,
and image digest. `junit-surface-{before,after}.txt` additionally bind the exact
installed `junit.jar` digest and public `TestResultSummary` signatures; the two
captures and the two container captures must be byte-identical.

This is deliberately a count projection, not a claim of complete
`TestResultSummary` support. The installed class also exposes `passCount` and
`duration`, but Fogell does not yet transport the duration float. Those members,
getter calls, rendering, indexing, mutation, identity/equality, truthiness,
reflection, and direct or nested hosted-step coercion remain catch-opaque
refusals. SCM-map returns also remain refused, so FG-177 stays PARTIAL.

`collect.sh` recreates a fresh, manifest-bound bundle in `/tmp` and prints its
path. It does not overwrite this retained run.
