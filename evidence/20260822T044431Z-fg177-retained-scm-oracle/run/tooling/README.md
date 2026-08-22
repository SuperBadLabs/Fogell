# FG-177 retained SCM-map oracle

Status: **tooling only; no live Jenkins capture has been run**.

This evidence-only lane measures history-dependent maps returned by `git` and
SCM-defined `checkout scm`. It does not change Fogell runtime code, the board,
or the ticket.

## Schedule and boundaries

The runner makes a closed Git fixture with byte-identical `Jenkinsfile` blobs:
main `A -> B -> C -> D`, feature `A -> F -> G`. Each producer gets its own new
retained Jenkins job and its own stable main/feature refs:

1. main A succeeds;
2. main B captures the map and then intentionally fails;
3. main C succeeds, separating previous B from previous-successful A;
4. feature F is the first observation of that branch;
5. feature G establishes positive feature history F/F;
6. main D establishes switch-back history C/C.

The runner records the raw TreeMap class, sorted keys, entry classes/values,
default rendering, and access/missing/index results. The validator asserts the
closed key/value/history contract and those admitted
non-mutating access markers. Rendering and wrong/null-index
text are retained as measurements only; their presence does not license a
general rendering or arbitrary-index model.

Each build also binds controller BuildData, terminal result, exact remote ref
before/after, archived workspace revision/payload, and both the submitted and
controller-returned job configuration. SCM definitions must name the exact URL,
branch, `Jenkinsfile`, and `lightweight=false`; inline definitions must contain
the exact rendered script with `sandbox=true`.

## Production capture

Production is the default and cannot be downgraded by caller-supplied tooling or
identities:

- `jenkins-driver.py`, `capture-controller-surface.py`, and the validator are
  fixed to the files beside this README;
- `FG177_ORACLE_DRIVER` and `FG177_RUN_ID` are refused;
- the run ID contains a UTC timestamp plus 128 bits from `openssl rand`;
- all ten remote targets (six pins and two refs per producer) must be absent;
- pins are created by one non-force atomic push and stable refs only advance;
- jobs are never deleted/reset; the driver must prove each generated job name
  absent before creation;
- the exact runner, driver, surface capture, validator, README, and case sources
  are copied into `tooling/` with a closed `SHA256SUMS` inventory;
- `fixture.bundle` retains the six Git heads and their complete object closure.

The live invocation needs explicit controller and fixture configuration:

```sh
export FG177_FIXTURE_PUSH_URL=ssh://fixture.example/repo.git
export FG177_FIXTURE_CLONE_URL=git://fixture.example/repo.git
export FG177_JENKINS_URL=http://127.0.0.1:8080
export FG177_JENKINS_USER=...
export FG177_JENKINS_TOKEN=...
export FG177_ORACLE_SSH_HOST=heman
export FG177_JENKINS_CONTAINER=exact-container-name
./run-retained-scm-oracle.sh /new/output/directory
```

Before any controller surface/build action, the runner invokes the accepted
`evidence/20260818-fg-177-measurement/verify-run-oracle.sh` against its retained
Jenkins 2.568.1 metadata. It exports that receipt's exact container ID as
`FG177_EXPECTED_CONTAINER_ID` for both surface captures, then repeats the
canonical verification after all builds. Both receipts and both immutable
metadata snapshots are retained and must be byte-identical. Production
validation requires the exact accepted metadata hashes and exactly 154 plugin
rows; a four-row fake manifest cannot pass.

The candidate is staged, closed by `MANIFEST.sha256`, validated, and only then
atomically published. Existing output is never overwritten. Unique remote refs
and Jenkins jobs are deliberately retained as evidence; cleanup is not part of
this capture.

## Hermetic tests are not evidence

Test adapters require both an override driver and the explicit gate:

```sh
FG177_HERMETIC=1 FG177_ORACLE_DRIVER=tests/fake-driver.py \
  ./run-retained-scm-oracle.sh /new/test-output
python3 validate-retained-scm-run.py --hermetic /new/test-output
```

The default validator rejects every `capture-mode.txt` other than
`production`. Conversely, `--hermetic` accepts only an explicitly hermetic
bundle and rejects contamination by production oracle artifacts. Hermetic tests
must therefore be updated to set `FG177_HERMETIC=1`, implement driver
`assert-absent`, accept the configure build-directory argument, emit
`submitted-config.xml`/`returned-config.xml`, and invoke the validator with
`--hermetic`.

No capture licenses multi-remote `GIT_URL_n`, custom GitSCM extensions/refspecs,
credentials/submodules, non-Git SCMs, whole-map hosted-step coercion, or any
unadmitted map operation. A real pinned capture and differential replay are
still required before SCM map returns may be implemented.
