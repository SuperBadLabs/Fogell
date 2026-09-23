# Independent final evidence review

Reviewed by the source/client implementation agent, independently of the pilot
and recovery harness author. This is a review of the retained evidence and
checker behavior, not an independent second live campaign.

## Pilot

`python3 scripts/prove-self-hosted-pilot.py --verify evidence/20260922-ai-e2e/pilot/receipt.json`
passed. The retained campaign contains 30 complete correction loops, 60 distinct
admitted builds/attempts, no unexpected failures and no censored runs. Correction
loop p50/p95 are 8.754/9.693 seconds; actionable diagnostic p50/p95 are
4.120/4.641 seconds. These measurements apply to the declared sequential local
Linux, fresh workspace, warm locked package-cache profile.

The verifier reopened each retained source envelope, JUnit report and SDK
artifact, recomputed their digests and source inventories, and checked the
planted failure and corrected test counts. Additional independent checks bound
every verified feedback page to its admitted attempt, manifest and parent loop;
all passed. There are exactly 30 distinct parent loop identities. The current
pilot harness hash equals the measured declaration's harness hash:
`e62c56e99ac304e1ccc097516d3174ff0e0d8d0f67534d027acf92a7f8b8e291`.

All six periodic retention phases converged with no held or pending work. The
first expired eight builds; the following five expired ten each. Each reduced
the measured physical state from 64.189–77.025 MB to about 12.846 MB. This is
evidence of bounded reclamation for the declared
workload, not a claim that application accounting enforces a filesystem quota.

## Recovery

The final retained recovery receipt reports 12.481 seconds through restored
artifact access and a fresh failed/corrected control pair. Independent review
recomputed every retained helper hash and the nested control receipt hash,
reopened its source/JUnit/SDK artifacts, validated both control runs and matched
their build IDs to the outer recovery receipt. These checks passed. The helper
checks the owned controller PID/start identity and queries the restored database
for the new terminal builds. Altered archive, changed state and missing state
controls were rejected; stale log and terminal publication were rejected after
restore-epoch activation.

Review found that the first database comparison hashed only physical lines
beginning with `INSERT`, omitting continuation lines of multiline text cells.
The harness now compares canonical complete JSON rows from every user table,
including duplicate rows and empty relation inventory, and sequence value/call
state, preserving PostgreSQL's canonical JSONB numeric precision. The original
archive and state checks remain in place. The seven portable
controls in `scripts/test-paired-recovery-inventory.py` pass, including a mutation
of only the second line of a cell and a high-precision numeric change. The final
recovery rerun passed with `row_encoding=sorted-complete-json-v1`, comparing 867
complete rows/sequences plus relation and schema inventories before activation.
The recorded recovery harness hash matches the current source:
`ccb99cdfc7ab541d3786dcb27e3b99d1b7c679c1dafb73cbebc1c17553f98a3a`.
This closes the incomplete-cell checker finding. Earlier review receipts remain
under `before-final-review`; final claims use the standard `pilot` and `recovery`
paths verified here.

The first preflight correctly rejected stale volatile .NET FIFO/socket entries.
Its failure and exact post-quiescence cleanup receipt are retained. The runbook
now requires process identity checks and narrowly recorded endpoint cleanup;
it does not authorize clearing state directories or deleting unidentified files.

## Scope

No outstanding correctness blocker was found in this final evidence review.
The corrected recovery rerun closes the checker issue.
This review did not rebuild, restart, restore, sweep or mutate a live deployment.
The proof applies to a quiesced disposable single-worker deployment; it does not
establish availability during concurrent writers or untrusted multi-tenant
workload isolation. Full release-gate results are recorded separately by the
parent agent. `git diff --check` passed after the review edits.
