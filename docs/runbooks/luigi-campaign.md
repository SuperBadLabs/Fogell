# Luigi deployment and adversarial feedback campaign, profile 1

This is the acceptance plan for [FG-270](../tickets/FG-270.md). It extends the
[local pilot](self-hosted-pilot.md) to a persistent, owned Luigi deployment.
Record the deployment declaration before measurement. A plan or provisioned
container is not a passing campaign; final receipts determine completion.

## Deployment boundary

Use a new rootless container for the controller and trusted worker, a separate
owned PostgreSQL container/database, and private persistent state/database
directories. Record full container IDs, immutable image digests, binary/source
hashes, runtime versions, SDK `10.0.301`, mount sources/options and ownership.
Mount application binaries and the controller token read-only. Keep token bytes
and connection secrets out of commands, logs and published evidence. Do not mount
the host container socket, broad home directories or unrelated state.

The controller uses `http://127.0.0.1:46206`; PostgreSQL must also have an explicit
loopback binding if published on the host. Check the actual listening addresses
and readiness, not only container arguments. An SSH tunnel may expose the API to
the operator's local loopback. Host networking shares Luigi's network namespace:
rootless execution and a read-only root filesystem do not prevent a trusted
pipeline from contacting other host services. Authentication remains mandatory;
local loopback is not a per-user access boundary. This profile makes no hostile
workload or untrusted-PR isolation claim.

Record finite controller and PostgreSQL CPU, memory and process limits and
verify their effective cgroup settings. A rejected or ineffective limit fails
preflight. Record every writable mount, including bounded scratch/tmpfs and any
package-cache writes. Persistent state and database storage must survive
container recreation; temporary filesystems do not satisfy that requirement.
The warm locked package cache has a recorded content inventory. A writable cache
is trusted shared input and its initial/final identity must be recorded.

Storage admission thresholds and retention accounting are **not hard quotas**.
Record free bytes/inodes, the minimum reserve and periodic abort thresholds for
each backing filesystem. Stop new admissions before the reserve is breached.
Do not attempt shared-host disk/inode exhaustion. Such a test requires a separate
enforced bounded mount; absence of that mount means no exhaustion claim. File
content-byte sums do not measure allocated blocks, PostgreSQL bloat or WAL size.

## Protect existing services

Before provisioning, save exact full IDs and `StartedAt` values for all five:
`ctrl`, `ag1`, `jenkins-bench`, `mcloving-faceoff2`, and `jenkins-lab`. Missing or
ambiguous entries fail preflight. Also retain running state, image identity,
ports, mounts and restart counts. Recheck after each destructive fault, after
recovery and at handoff. Their IDs and `StartedAt` must be byte-for-byte unchanged.
This proves the protected containers were not replaced or restarted; it does
not establish zero latency impact on their workloads.

## Operator access

The installation belongs to `srikanth` on Luigi. Its nonsecret configuration is
`/home/srikanth/services/fogell/deployment.json`; read the current controller ID
and release path there after an upgrade. The API is loopback-only. Check or
restart the owned service from HeMan:

```bash
ssh luigi 'curl --fail --silent http://127.0.0.1:46206/health/ready'
ssh luigi 'systemctl --user status --no-pager fogell.service fogell-postgres.service'
ssh luigi 'systemctl --user restart fogell.service'
```

For local API access, keep this tunnel open:

```bash
ssh -N -L 46206:127.0.0.1:46206 luigi
```

Authenticated requests still require the owner's private token file. To keep
credentials on Luigi, run the [feedback client](feedback-client.md) inside the
recorded controller with `podman exec FULL_CONTROLLER_ID dotnet
/app/tools/Fogell.Client/bin/Release/net10.0/Fogell.Client.dll COMMAND OPTIONS`.
Read the current full controller ID, URL, organization and project from
`deployment.json`, and use `--token-file /run/fogell/token` inside the container.
Pipeline/source paths must be readable inside that container. For example,
owner-staged files under `services/fogell/state/client-inputs` are available at
`/data/client-inputs`. The host does not need a separate .NET installation.
Never put token bytes in arguments or evidence.

Persistent workspace state is under `services/fogell/state`; PostgreSQL uses
the owned `fogell-postgres-20260923` volume. Private paired backups are under
`services/fogell/backups`, and measurements under `services/fogell/campaigns`.
Do not copy backup directories into public evidence: each restore drill includes
a private connection environment. The evidence exporter selects receipts and
hash inventories and checks for the installation's actual credential bytes.

Both user services are enabled and user lingering is enabled. Those settings
support persistent operation after logout; the campaign does not reboot Luigi.
Stopping just `fogell.service` quiesces the controller and worker while keeping
its database online. Stop `fogell-postgres.service` only after the controller
when intentionally taking the whole owned deployment down.

The benchmark invokes bounded retention explicitly; it does not install an
unattended retention timer. The 2 GiB state guard belongs to the campaign
supervisor. It is neither a permanent service disk quota nor a database/WAL cap.
Use the [retention runbook](retention.md) to choose an operating retention policy
before starting unattended workloads.

Every mutation must resolve to an owned full container ID, recorded PID/start
identity, private database or private directory. Refuse protected IDs, unknown
ownership or a changed identity. Never use host-wide process kills, container
prune, broad name matching, global network changes or cleanup outside the owned
deployment. On failure, retain evidence and restore only the owned service to a
known state. Do not tear down the successful persistent deployment at handoff.

## Baseline acceptance

Keep deployment versions separate. Complete the originally declared 100-loop v1
baseline without changing its binaries or declaration, then capture the strict
v1 adversarial result. Preserve typed-cap diagnostic failures as failures. Gate
the narrow correction, install a new immutable v2 release, and run
`prepare.py --refresh` to archive the prior declaration and refresh the active
container identity, smoke command and tool pin.

The v2 campaign exposed a second missing typed reason after controller-crash
lease expiry. Retain that failed receipt and its unobserved queued controls.
Gate the atomic expiry/restore diagnostic correction and install immutable v3.
Run the complete v3 adversarial campaign and paired recovery **before any
additional retention sweep**, preserving the v1 loop-100 corrected artifact.
Then run a separate 30-loop v3 qualification with six retention phases. Verify
its artifacts and report its own latency distribution and final service health.
Final deployed-v3 acceptance requires its adversarial controls, restored
correction pair and all 30 qualification loops to pass. Do not relabel v1's 100
loops as v3 evidence or pool the versions' timings. Link baseline, failures,
fix/gate and qualification receipts by their immutable source/binary/tool hashes.
The workload and timing thresholds below also apply to v3's 30 loops, with 60
distinct admissions and 30 parent loop IDs.

Run `scripts/prove-self-hosted-pilot.py` on Luigi with `--loops 100`, the explicit
loopback URL, token file, organization/project, deployed client, pinned tool
digest, deployment declaration, cache, private work/output/state paths and owned
retention command. The source pack must bind the actual selected Domain source,
pipeline, parent loop and tool identity. Do not reuse an old receipt or submit
different source while reporting the declared hashes.

The condition is fresh workspace/build outputs and warm locked NuGet cache,
one worker and sequential correction loops. Each of 100 loops runs a planted
failure then the corrected real Domain library and all 41 tests: 200 unique
admissions/attempts, 100 parent loop IDs. The failed run must return unstable
with the named structured diagnostic; the corrected run must return success.
All test counts, JUnit/SDK artifacts, source identities, tool pins and terminal
feedback cursors must verify. No unexpected failure, missing result, censored
run or replaced denominator is permitted for a passing baseline.

Retain admission, first output, actionable diagnostic and terminal timings on
the monotonic clock. Report p50/p95 plus failures and censored counts. Baseline
acceptance is p95 correction-loop duration at most 240 seconds, each build at
most 120 seconds, and the existing harness campaign ceiling of 5,400 seconds
(90 minutes). An externally imposed shorter deadline must remain visible as a
failure/censored campaign; it cannot become a successful shortened run.

Exercise retention every five loops: twenty phases must converge within the
declared finite sweep budget, preserving protected lineage and reclaiming
eligible evidence. Reopen retained local artifacts/source packs with `--verify`
after measurement. Record filesystem/DB usage separately from logical retention
accounting. Keep stressed fault observations separate from the baseline latency
distribution rather than selecting only fast successful samples.

## Bounded adversarial phases

Before starting the driver, save its hash and exact phase matrix: input counts,
concurrency, request/output byte limits, per-request and per-phase deadlines,
fault hold durations, maximum restart count and total deadline. Each field must
have a finite value; an open-ended stress loop is outside this profile.

The driver profile declares 32 concurrent unique simple submissions, all 32
successful and uncensored for a passing load phase; 16 concurrent identical-key
submissions with one new admission and one shared build/attempt identity, followed
by changed-content conflict; shell exit 7 with a typed workload diagnostic; a
one-second timeout; and one finite 40 MiB output fixture against the runner's
16 Mi UTF-16-code-unit capture cap. A 2 MiB artifact must produce the named
`ARTIFACT_LIMIT_EXCEEDED` failure against the 1 MiB per-file limit and publish no
artifact. A finite
300 MiB write must encounter ENOSPC only on the verified 256 MiB `/tmp` tmpfs,
clean its unique temporary directory, and allow the subsequent control to pass.
Run cancellation, owned-runner termination, owned-controller termination/restart
and owned-PostgreSQL stop/start three times each, with two queued success controls
per fault. Bound observation waits to 120 seconds, requests to five seconds,
ordinary subprocess calls to twenty seconds and service operations to thirty
seconds, the adversarial campaign to 3,600 seconds and
aggregate raw HTTP capture to 256 MiB. Record stricter effective limits if used.
No mutation POST is automatically retried after a transport error. The fault
driver must verify the exact runner/container/unit ownership before termination;
a broad process-name match is insufficient.

The PostgreSQL fault declares `postgres_outage_hold_seconds: 20` and holds the
owned database service stopped for at least twenty measured seconds after
readiness returns 503. The fixture remains fifteen seconds, so this interval
crosses the ten-second lease-renewal period and potential workload completion.
Record hold start/end on the campaign monotonic clock and actual `hold_ms`;
the offline verifier requires at least 20,000 ms and matching stop/start command
and readiness observations. Sleeps are at most 200 ms with campaign-budget
checks. Cancellation or budget expiry still restores the owned service through
its existing `finally` path.
Verify both controls are admitted and actually queued before each fault on the
single worker. Authentication, malformed pipeline and oversized pipeline controls
must return 401, 422 and 413 respectively; the oversized body is exactly 16 MiB
plus one byte. Stop further fault injection after a failed phase, preserve the
failure and attempt the final healthy control. The scratch test stays inside
the verified container tmpfs and never fills host/state storage.

Required controls cover invalid authentication and malformed/oversized inputs;
idempotency replay versus conflicting content; a finite burst of concurrent
admissions and feedback reads; cancellation of owned work; and interruption and
restart of the owned controller. A PostgreSQL interruption, if declared, may
target only the owned database container and must have an automatic bounded
release. Never stop the host database or a protected service.

Acceptance follows the actual public contract: bad requests are rejected,
replay identifies the same admission, conflict creates no second execution,
feedback remains bounded and cursor-consistent, and successful observations bind
the admitted source/tool. Under cancellation or process interruption, an honest
reconciliation-required result is acceptable where authority is uncertain;
fabricated terminal success or automatic replay of uncertain effects is not.
Capture each expected rejection and each unexpected result. After each fault,
verify readiness and complete its two queued success controls before the next
phase. The final recovery additionally requires a real Domain correction pair.
No false pass may arise from a dead driver, missing output or
an exception swallowed by cleanup.

## Final recovery and persistent handoff

Quiesce only the owned controller/workers, prove writers extinct and preserve a
retained terminal artifact. Use `scripts/luigi/recovery.py`, which adapts the
complete-JSON comparator and controls from `scripts/prove-paired-recovery.py` to
the owned service. It binds private connection endpoints to the exact PostgreSQL
container and verifies refreshed deployment/tool/smoke identities before stopping
the controller. Back up the paired database/state into a new private directory;
restore into a new empty database and a new private state root. Apply the narrow
stale-runtime-endpoint
cleanup procedure in the [pilot runbook](self-hosted-pilot.md) if required.

Require zero lost backed-up rows/sequences/state files, rejection of corrupted
backup controls, restore-epoch advancement, rejection of pre-backup log and
terminal authority, API retrieval of the original artifact and a fresh real
failed/corrected control pair. Recovery must complete within 120 seconds from
the declared restore start through the new pair. The timer excludes incident
detection, quiescence and backup creation; report those boundaries explicitly.

The temporary restored controller uses the v3 image/release and original port
while the persistent controller is stopped. It retrieves the retained v1 artifact
and executes a new v3 correction pair; receipts keep those identities separate.
Exact temporary container identities are journaled before creation and recovered
after interruption. Only after the restored stale-writer proof and correction
pair pass may the source probe enter explicit fenced reconciliation. It must
never receive a fabricated terminal success. An incomplete drill retains the
original probe lease rather than weakening the stale-writer control.

The adapter removes only its temporary controllers/tools and restarts the
original service in `finally`, verifying readiness and listener ownership. If
temporary authority cannot be proven extinct, it fails closed and records the
cleanup error rather than start competing controllers. Restored database/state
and backups remain private. Backup directories contain private connection
environment files: publish selected receipts/hashes only, never the entire tree.

Leave exactly one intended active controller authority and its database/state
pair, with readiness and a documented restart mechanism. This drill hands off
the original persistent database/state pair running the qualified v3 release;
the disposable restored pair remains inactive. Confirm availability after the
provisioning SSH session ends.
Do not claim host-reboot survival unless the relevant user service/linger setup
and reboot were actually verified. Keep private credentials accessible only to
the owner and publish token-file usage, endpoint/tunnel instructions, service
identities, state paths, restart/stop instructions and retained evidence paths.
Recheck all five protected services at final handoff.

The final receipt links the predeclared configuration, baseline verification,
bounded fault results, retention observations, paired recovery, protected-service
comparisons and persistent-service health. The [2026-09-23 measured campaign](../../evidence/20260923-luigi/README.md)
passed every required acceptance item; FG-270 is DONE. Future campaigns require
their own complete receipts. Jenkins breadth, cold-cache performance,
host power-loss durability and untrusted isolation are outside this claim.
