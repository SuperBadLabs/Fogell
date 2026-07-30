# Corpus reference

Fogell does not vendor the corpus. It pins it by hash.

- Source of truth: `/sn8100/work/exchange/crucible-gate/corpus/jenkinsfiles`
  (228 files, selection method pre-registered in that directory's `METHOD.md`)
- `CORPUS-SHA256SUMS` is the pinned manifest copied at workspace creation
- Jenkins oracle verdicts per file (lint / compiled / reached-agent) live in the
  sealed Mario run referenced from `docs/architecture/BASELINE.md`

Any harness that scores Fogell must verify this manifest first. A corpus that
drifts silently invalidates every number in `BASELINE.md`.
