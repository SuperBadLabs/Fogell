# Live parity re-derivation against Jenkins 2.568.1 — 2026-09-06, mario

The boards are self-reported. This re-derives parity by measurement, live,
against a running Jenkins 2.568.1, and reports where the boards hold and where
a comfortable reading of them does not.

## 0. The boards' counters are current, not stale

`scripts/bin/generate-scorecard` was rebuilt with fflat and re-run against the
current tree. It produced output **byte-identical** to the committed
`COMPATIBILITY-SCORECARD.md` and `COMPATIBILITY-LEDGER.tsv` — `git status`
clean. `tier1=12, admitted=188, tier3=28 of 228`; `307 proven of 307 expected`.
An independent re-score of the corpus with current main agreed (200 admitted /
28 rejected). The 300-files-vs-307-receipts gap is not a discrepancy: expected
receipts are derived from what cases *produce*, and some produce more than one.

## 1. Hand-written differential suite, re-run live: 292 / 307

`fogell-diff` against `http://127.0.0.1:18086`, core 2.568.1, **with workspace
hashes compared** (`FOGELL_JENKINS_WORKSPACE`), all 300 cases.

**All 15 non-proven are rig artifacts, not behavioural divergence:**

| cause | n | evidence |
|---|---|---|
| container `$HOME` / workspace path | 4 | `jenkins=/var/jenkins_home` vs `fogell=/tmp/fogell-diff-…/_agent_home/…` |
| git binary version | 4 | `jenkins=git version 2.47.3` vs `fogell=git version 2.43.0` |
| stale workspace `.git` | — | `jenkins=git rev-parse --resolve-git-dir` vs `fogell=Cloning the remote Git repository` |
| SCM not configured | 5 | harness reported `NOT-COMPARABLE` itself |

This ran against `jenkins-faceoff` — same pinned 2.568.1 core but 360 plugins
from `bench-jenkins-home`, not the sealed oracle's 90. **The board's 307/307 is
not contradicted**, and the exercise demonstrates why a pinned oracle is
required: an unpinned rig manufactured 15 false divergences from environment
alone.

## 2. The 228 corpus — acceptance parity, the only boundary measurable

The corpus is **never executed** here: untrusted third-party Jenkinsfiles, and
only 16 rows are allowlisted for execution behind the FG-244 fence. So this
compares the boundary that can be measured without running anything: Jenkins'
Declarative linter (`/pipeline-model-converter/validate`), which parses and
validates but starts no build.

**Method validation.** The live linter finds **80** of 228 Declarative-valid —
reproducing `docs/architecture/BASELINE.md`'s inherited "Jenkins
Declarative-valid | 80" **exactly**. Method and inherited baseline corroborate
each other.

### Result

93 files took Fogell's declarative path; 135 took its scripted path.

| | jenkins=valid | jenkins=invalid |
|---|---:|---:|
| **fogell=ok** | 68 | 10 |
| **fogell=err** | 12 | 3 |

Raw agreement **71 / 93 = 76.3%**. Of the 22 disagreements, **8 are rig
artifacts** — seven are `Tool type "maven" does not have an install of "X"
configured` (the linter validates tool names against *configured installations*,
and this Jenkins has no Maven tool), one is an unconfigured `githubPush` trigger
plugin. Excluding those: **71 / 85 = 83.5% genuine agreement**, with 14 real
divergences.

### The 12 false rejections — and a correction

**Twelve files are refused by Fogell (`malformed_syntax`) and accepted by real
Jenkins.** This is the dominant failure mode, and it runs *away* from parity.

**Ten of those twelve are exactly the files earlier characterised in
`bench/standoff-2026-09-06` as "deliberate fail-closed tightening, consistent
with your doctrine."** That reading was wrong and is corrected here: measured
against Jenkins, they are false rejections, not safety. A refusal is only
fail-closed if Jenkins also refuses; when Jenkins accepts and Fogell does not,
that is a compatibility regression wearing a safety costume.

### The 2 genuine over-acceptances

| file | Jenkins says |
|---|---|
| `k11h-de_zap-jenkins` | `unexpected token: < … def gitCredId = <jenkins-cred-id>` — an unreplaced placeholder |
| `nikoly_selenium-grid-docker` | `Expected a step @ line 27 … try {` — bare `try` where Declarative wants a step |

Same shape as the over-acceptance found independently in
`bench/standoff-2026-09-06`: Fogell admits input real Jenkins rejects.

## 3. What cannot be measured

**135 of 228 files (59%) are scripted**, and the Declarative linter cannot
adjudicate them. Obtaining Jenkins' verdict would mean CPS-compiling untrusted
third-party code by starting a build — which the repo forbids and which no
result here is worth. Acceptance parity for the majority of the corpus is
therefore **unmeasured, and not measurable under the current control**.

**McLoving cannot be parity-tested against Jenkins at all.** It has no
Jenkinsfile execution path — it consumes its own YAML and its compiler provably
never evaluates Groovy. Direct Jenkins parity is a Fogell-only property.

## Limits

One run, non-sealed bench Jenkins (360 plugins vs the oracle's 90), no Maven or
SCM tooling configured. The linter validates Declarative structure, not runtime
behaviour: a file it accepts may still fail when run. None of this is sealed
evidence and none of it carries an FG-005 seal.
