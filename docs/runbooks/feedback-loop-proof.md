# Failure → correction feedback proof (FG-265)

This is a small, trusted execution experiment against a disposable controller.
It submits two repository-owned pipelines through the F# client: one shell
command prints an expected assertion failure and exits 1; the corrected command
prints an expected passing assertion and exits 0. This proves the transport and
execution loop, not that an agent diagnosed or automatically repaired code.
Each fixture deliberately sleeps one second after printing its marker, to expose
progressive feedback. The checker requires nonempty output on a nonterminal page
before completion; observed terminal times include that deliberate delay.

Build the controller, Run.Host and client in Release. Provision a separate
PostgreSQL database, runtime role, organization/project and controller state root
using the host runbook. Use only resources owned by this campaign. The harness
requires that configured deployment; it never starts, deletes or stops a shared
database or container. The deployment owner must stop and remove its resources
afterward. A watch timeout does not cancel a controller build.

```bash
python3 scripts/prove-feedback-loop.py \
  --url http://127.0.0.1:YOUR_PORT \
  --organization YOUR_ORGANIZATION_UUID \
  --project YOUR_PROJECT_UUID \
  --token-file /absolute/private/token \
  --controller-binary src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host.dll \
  --controller-identity 'source revision and explicit clean/modified tree description' \
  --environment 'single local controller; warm binaries; state and database newly created' \
  --output /tmp/unique-new-feedback-receipt
```

The output directory must not already exist. Each invocation generates unique
idempotency keys. Defaults: 100 ms polling, 10 s individual HTTP deadline, 60 s
watch deadline, 1 MiB client response limit, and 4 MiB combined stdout/stderr per
process. The harness gives each client process ten additional seconds to start
and finish, then kills only its own process group. It retains raw client output
and a failure receipt when a command fails. These trusted fixtures contain no
credentials; nevertheless, inspect evidence before publishing it.

`receipt.json` includes build/attempt identities, pipeline hashes, client DLL
hash, optional controller DLL hash, environment description, response records,
limits and monotonic observations. DLL hashes identify those files, not the
entire dependency closure. Source identity must explicitly describe uncommitted
changes when present. No credentials are recorded.

Timing starts immediately before spawning each submission client. Admission
duration includes client startup, HTTP and process completion. First nonempty
feedback and terminal observation are measured when the harness receives complete
JSON lines from the watch client, relative to the same submission origin. Client
`client_elapsed_ms` fields are retained separately with their own process origin.
These numbers include polling and transport; they are not server queue latency,
time to diagnosis, or p50/p95. A single failure/correction pair establishes no
performance distribution or release target.

Recheck a saved receipt against the current repository fixture hashes:

```bash
python3 scripts/prove-feedback-loop.py --verify /tmp/unique-new-feedback-receipt/receipt.json
python3 scripts/prove-feedback-loop.py --self-test
```

The checker requires the known failure, expected output, a drained authoritative
success from the corrected submission, distinct build/attempt identities, cursor
continuity, monotonic observations, matching exit statuses, and fresh admissions.
It also rejects ten deterministic altered receipts: false failure-fixture
success, missing output, undrained terminal output, a false successful watch exit,
reused submission keys, wrong build identity, a cursor gap, nonfinite timing and
regressed timing, and all output collapsed into a single terminal page. Saved
receipts explicitly marked failed are refused too.
These controls test checker refusal without a timing
race; the synthetic self-test is explicitly not real-controller evidence.
The self-test additionally exercises subprocess deadline and aggregate output
guards three times each using local Python children; it kills and reaps each
harness-owned process group and removes its temporary output.

For a retained campaign, copy the receipt and concise environment/cleanup notes
into an evidence directory. Record failures too. Do not claim completion until
the real-controller receipt passes and deployment cleanup is confirmed.
