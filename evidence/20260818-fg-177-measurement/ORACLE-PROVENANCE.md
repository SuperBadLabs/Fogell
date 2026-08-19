# FG-177 Jenkins oracle provenance

The 2026-08-18 measurements are owned by one controller identity. A core
version alone is insufficient because Pipeline step descriptors and return
objects are supplied by plugins.

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
