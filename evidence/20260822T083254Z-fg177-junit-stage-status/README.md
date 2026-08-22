# FG-177 JUnit stage-status oracle

This tooling captures the measured two-by-two interaction between JUnit's
`skipMarkingBuildUnstable` and `skipMarkingStageUnstable` flags against pinned
Jenkins 2.568.1 with JUnit plugin `1416.vd753e036de5e`.

The ordinary Fogell differential trace compares terminal result, normalized
console output, and workspace state. It deliberately excludes Pipeline graph
annotations and carries no stage-result field. Those three observables cannot
distinguish an ignored stage flag. This bundle therefore captures Jenkins'
`/wfapi/describe` response before deleting each fresh job and reduces it to the
declared `probe` and `later` stage statuses. A normal differential receipt may
be collected beside this evidence, but it is not the proof of stage decoration.

`expected.tsv` is the closed matrix. Each case also proves the typed JUnit
summary remains `4,2,1`, the current stage continues, stage/pipeline post arms
select the measured result, and `skipStagesAfterUnstable()` observes the build
projection.

The collector refuses reused jobs, attributes the exact queue item/build,
requires terminal build and wfapi responses, captures submitted and returned
configuration, build JSON, console, raw and canonical stage data, per-stage
wfapi detail, and a canonical workspace manifest/hash before cleanup. Before
and after snapshots bind Jenkins core, session hash, immutable controller and
image, the complete plugin inventory, and every installed jar under JUnit,
pipeline-rest-api, pipeline-graph-analysis, workflow-api, and
pipeline-model-definition. The staged run is validated before one atomic
directory rename publishes it, then `MANIFEST.sha256` binds every file.

One measured pipeline-rest nuance is preserved rather than normalized away:
in `b0s0`, `skipStagesAfterUnstable()` prevents the `later` body from running
(the workspace has no `later.txt`), while `/wfapi/describe` classifies that
declared stage as `SUCCESS`, not `NOT_EXECUTED`. The matrix therefore binds both
the API status and the absent effect instead of treating either as a proxy for
the other.

Run from the repository root with:

```sh
bash evidence/20260822T083254Z-fg177-junit-stage-status/collect.sh
```

The default oracle is `http://127.0.0.1:18099`, container `jenkins-lab` on
`luigi`. Authentication may be supplied with `FG177_JENKINS_USER` and
`FG177_JENKINS_TOKEN`. Set `FG177_RUN_DIFFERENTIAL=1` to additionally build the
current Fogell CLI and collect ordinary receipts after the stage oracle. That
hook is optional because stage-status compatibility is not represented by the
current receipt contract.
