# FG-177 Jenkins oracle provenance

The measurement cases were registered on 2026-08-18. Their retained receipts
and logs were regenerated on 2026-08-19 only after the shared verifier accepted
one controller identity. A core version alone is insufficient because Pipeline
step descriptors and return objects are supplied by plugins.

The committed metadata is captured read-only from the HeMan bastion with:

```sh
FOGELL_JENKINS_URL=http://127.0.0.1:18099 \
FOGELL_JENKINS_HOST=luigi \
FOGELL_JENKINS_CONTAINER=jenkins-lab \
  bash evidence/20260818-fg-177-measurement/jenkins-oracle.sh \
    capture /tmp/fg177-jenkins-oracle
```

Review a recapture rather than overwriting the pin:

```sh
for file in jenkins-core.txt jenkins-plugins.tsv jenkins-controller-image.txt
do
  diff -u \
    "evidence/20260818-fg-177-measurement/$file" \
    "/tmp/fg177-jenkins-oracle/$file"
done
```

Pinned identity captured on 2026-08-19:

- Jenkins core: `2.568.1`;
- installed plugins: the complete stable-sorted
  `shortName<TAB>version<TAB>active<TAB>enabled` manifest in
  `jenkins-plugins.tsv` (154 rows, SHA-256
  `6af6817f555a6fbbbcb5b41d48e7be58b00517efc913b21cfb825d0614630b3a`);
- controller image name: `localhost/jenkins-rich:local`;
- immutable local image ID:
  `7a193ff741388715adcb359f604ea7da4e9c5de7c87105e390af1410b3677602`;
- image digest:
  `sha256:8f97ec730facd02d740132bcc494b4bdecef90030ee55b9095b3fd253f1db332`.

`run-probes.sh` and `run-archive-schema.sh` call `jenkins-oracle.sh verify`
before any evidence-side mutation. Verification requires HTTP 200 without
redirects from both the controller root and plugin API, exactly one
case-insensitive `X-Jenkins` header with the pinned value on each response, an
exact complete plugin-manifest match, and an exact live container-image match.
Refusal creates no output directory, builds no CLI, synchronizes no fixture,
and writes no receipt or exit marker.

Both runners resolve the requested Jenkins identity once. An unset
`FOGELL_JENKINS_CORE` selects the evidence default `2.568.1`; an explicitly
empty or noncanonical value refuses before oracle I/O. The resolved URL, core,
host and container are passed as command-local environment to the verifier,
so its child process does not depend on whether the runner's shell variables
were exported. The identical resolved URL/core pair labels the differential
CLI invocation, and the manifest writer receives that core explicitly and
refuses unless it equals the verified metadata. Host/container also drive the
workspace collectors, preventing verifier/collector coordinate drift.

## Verified receipt recapture

From the repository root on HeMan, the retained evidence was recaptured once,
in this order:

```sh
bash evidence/20260818-fg-177-measurement/run-probes.sh
bash evidence/20260818-fg-177-measurement/run-archive-schema.sh
```

Both commands returned `1`, the expected differential status for the retained
divergences. Each runner records its start time before verification, its
verification-complete time before the CLI build, and its finish time after the
CLI and exit marker. It then atomically publishes an adjacent manifest:

- `runs/probes/probe-run-manifest.tsv` binds the four ordered probe cases and
  receipts;
- `runs/archive-schema/archive-schema-run-manifest.tsv` binds the
  archive-schema case and receipt.

Each runner publishes a self-contained bundle:

- `runs/probes/` contains the probe manifest, log, exit marker, rendered cases,
  and its exact four-file `raw-receipts/` set;
- `runs/archive-schema/` contains the archive manifest, log, exit marker, case,
  and its exact one-file `raw-receipts/` set.

Each manifest records the Jenkins core, the hashes of the core, plugin and
image metadata, the complete plugin-manifest hash and row count, the controller
image name, immutable ID and digest, the run log and exit-marker hashes, the
ordered rendered case hashes, and the resulting receipt hashes. The manifest
is not published if any input is missing, the timestamps are out of order, or
the exit marker disagrees with the captured CLI status. Git commits the
manifest and every bound file together, making the recapture independently
auditable without relying on filesystem modification times.

## Transactional publication

The CLI never writes into a previously published receipt directory. After
oracle verification, the runner takes a per-run lock and creates an empty,
same-filesystem sibling stage under `runs/`. Only CLI statuses `0` (proved) and
`1` (completed divergence) are publication-eligible. Before writing the
manifest, the runner requires the receipt directory to contain exactly the
expected regular filenames, with no symlinks, extras, missing files, or empty
files. Because that directory began empty, every accepted receipt is from the
current invocation; the manifest records its fresh content hash.

`publish-run-bundle.py` then uses Linux `renameat2(RENAME_EXCHANGE)` to replace
an existing bundle in one filesystem operation, or an ordinary same-filesystem
rename for the first publication. It deliberately has no copy/delete fallback.
If the platform cannot exchange directories atomically, publication refuses.
An interruption before the exchange leaves the prior bundle visible; an
interruption after it leaves the new complete bundle visible. The displaced
bundle or incomplete stage is only an ignored hidden sibling cleaned by the
runner's bounded exit trap. Thus receipts, log, exit marker and manifest cannot
be published as a stale/new mixture.
