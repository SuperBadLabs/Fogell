# Scope of the archived SCM map observations

The archived `fg177-probe-return-semantics` and
`fg177-probe-checkout-scm` receipts came from fresh jobs whose first build was
created for the probe. Their case-header phrase “complete sorted key set” means
that the probe enumerated every key in the single map returned by that build.
It does **not** establish the complete API surface across retained job history.

Accordingly, the observed build-1 keys are minimums only:

- `git`: `GIT_BRANCH`, `GIT_COMMIT`, `GIT_LOCAL_BRANCH`, `GIT_URL`;
- `checkout scm`: `GIT_BRANCH`, `GIT_COMMIT`, `GIT_URL`.

Before slice 4 can define a closed map, retained multi-build Jenkins evidence
must enumerate every build's map and explicitly inspect
`GIT_PREVIOUS_COMMIT`, `GIT_PREVIOUS_SUCCESSFUL_COMMIT`, and any other
history-dependent keys. `fg177-plan-git-history.Jenkinsfile` is the committed
two-build plan for the `git` half. It is rendered for inspection but is not
part of `run-probes.sh`, so its presence is not evidence that it ran.
SCM-defined sequences are currently rejected by the differential harness;
`checkout scm` therefore remains blocked on a harness extension or a dedicated
retained Jenkins job. No history-dependent value is inferred here.
