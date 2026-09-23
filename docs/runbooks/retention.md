# Operator retention (FG-268)

`Fogell.Retention` expires terminal execution payload under an explicit
organization policy. It is an operator tool using the maintenance connection;
it is not a worker capability or automatic unattended scheduler. Read the
[ownership/state-machine design](../architecture/RETENTION.md) first.

The supported implementation requires Linux x64, a filesystem exposing statx
mount/inode/birth identity, and working descriptor-relative rename/unlink/fsync.
It accepts mutually trusted controller workloads. It does not protect against a
malicious process with the controller's own operating-system identity actively
rewriting private quarantine names. Active and uncertain executions, every
retry lineage, nonconfirmed effects and held deletion journals are protected.

Set `FOGELL_MAINTENANCE_DATABASE_URL` through the operator's existing secret
environment. The tool never prints that value. Supply the exact paired state
root and organization. Before selection, the materialized pipeline transport
must match the immutable database source digest; snapshot builds also verify the
snapshot transport and decoded pipeline. Missing or disagreeing definitions
refuse selection. Pre-execution terminal builds with no definition transport are
therefore retained for operator investigation.

```bash
dotnet build tools/Fogell.Retention/Fogell.Retention.fsproj -c Release
timeout --kill-after=5s 40s \
  dotnet tools/Fogell.Retention/bin/Release/net10.0/Fogell.Retention.dll \
  --state-root /absolute/paired/controller-state \
  --organization YOUR_ORGANIZATION_UUID \
  --keep-builds 100 \
  --max-bytes 1073741824 \
  --max-age-seconds 2592000 \
  --max-candidates 1000 \
  --max-entries 10000 \
  --max-operations 256 \
  --budget-seconds 30
```

This command **deletes selected evidence**. There is no `--dry-run` switch;
unknown/duplicate switches and invalid/overflowing numeric values refuse before
connecting. Schema installation remains the existing deployment operation;
`Fogell.Retention.dll migrate` exposes the same checksum-locked migration runner
for a separately authorized maintenance deployment.

Policies apply to eligible terminal builds. Age is measured from the latest
build event, or creation time when no event exists. Newest eligible builds are
retained within count and logical-byte limits, with age expiry independent of
those limits. Byte accounting includes file lengths, UTF-8 log and diagnostic
payload bytes, and the source-envelope bytes in build definitions. It excludes
irreducible identity/audit records, filesystem allocation overhead, and protected
builds. PostgreSQL deletion makes space reusable; it does not promise immediate
physical database-file shrinkage. Open artifact streams can temporarily retain
unlinked file blocks.

`MaxCandidates` bounds the eligible history inventory and `MaxEntries` bounds
filesystem entries across that sweep's plans. Exceeding either refuses the
uncommitted plan. The planner does not bypass an oversized or disagreeing build
to claim success. Increase a bound deliberately after inspecting ownership, or
resolve the disagreement; never replace this with an unrestricted recursive
delete. The operation count bounds filesystem entries and database log rows,
with one operation for final source-envelope expiry. Per-command SQL deadlines
are five seconds and lock waits two seconds. The caller's outer deadline also
bounds a stalled command; filesystem I/O inherits the host's kernel behavior.

Each JSON result reports `Selected`, `Expired`, `Operations`, `Pending`, `Held`,
`ReclaimableBytesBefore`, `SelectedLogicalBytes`, and `ElapsedMs`. Pending work is
normal: invoke the same sweep again to resume its durable deletion cursor.
Exit 0 means that sweep finished without a hold, not necessarily that `Pending`
is zero. Exit 2 reports a held journal; exit 1 is invocation/planning/transport
refusal. Inspect `build_retention.error` in the selected tenant when a hold occurs.
Do not delete its journal, rewrite its cursor, or turn it back into `selected`
without an operator-reviewed reconciliation of the exact preserved filesystem.

Selection immediately withdraws the build's evidence: logs, feedback, artifacts
and source retrieval return HTTP 410 `evidence_expired`. Build/attempt IDs and
status remain durable. Payload expiry copies source/admission digests to the
tombstone first; repeating the old admission key refuses expired evidence.
Use a new deliberate submission for a new run. No retention operation creates a
retry, repairs uncertain effects or changes terminal execution truth.

The [retained focused proof](../../evidence/20260922-retention/README.md) covers
23 safety tests against fresh PostgreSQL databases, repeated three times. Each
test run creates and drops only its UUID-named database and private temporary
state trees. The real feedback/pilot campaign records workload capacity recovery
separately; policy bounds and workloads must accompany any such claim.
