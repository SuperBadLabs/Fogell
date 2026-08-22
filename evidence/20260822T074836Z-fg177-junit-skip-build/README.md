# FG-177 JUnit build-result suppression evidence

This bundle binds two Jenkins 2.568.1 / Fogell differential runs for JUnit
plugin `1416.vd753e036de5e`.

`fg177-junit-skip-marking-build-unstable` publishes a failing four-test report
with `skipMarkingBuildUnstable: true`. Both engines return the typed `4,2,1`
summary, continue the current stage, run a later stage despite
`skipStagesAfterUnstable()`, select pipeline `post { success }`, and finish
`success` with identical ordered output and workspace content.

`fg177-junit-skip-marking-build-unstable-false` is the presence-is-not-truth
control. Explicit `false` returns `2,1,0`, continues the current stage, selects
`post { unstable }`, and finishes `unstable` on both engines.

The oracle is checked before and after the run against the retained FG-177 pin:
154 plugins, Jenkins session identity, immutable container identity, and image
digest. `junit-surface-{before,after}.txt` additionally bind the installed
`junit.jar` digest and public `TestResultSummary` signatures; the paired
captures must be byte-identical.

This proves build-result suppression only. Jenkins' pipeline-node/stage warning
decoration, `skipMarkingStageUnstable`, non-boolean coercion, `passCount`, and
`duration` remain outside Fogell's modeled surface.

`collect.sh` recreates a fresh manifest-bound bundle in `/tmp` and prints its
path. It does not overwrite this retained run.
