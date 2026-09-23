# Bounded terminal evidence retention (FG-268)

The operator owns policy; PostgreSQL owns eligibility and the deletion journal.
The tool never infers a build identity from an arbitrary directory name. It
derives known paths only from tenant/build/attempt rows. Build, attempt, event,
effect and definition identity records remain durable controller truth. Source
envelope bytes expire only after copying their request digests into the tombstone;
verified source identity metadata remains readable.

Owned payload roots include each build workspace, the agent HOME keyed by
SHA-256 of `buildKey + NUL + buildNumber`, build-number-scoped stash, artifact
staging, attempt artifact snapshot, bounded-pool runtime scratch, definition
transport, journal and current-fence event/containment paths. Shared parent
directories are not recursively deleted.

Selection holds the tenant advisory sweep lock and locks candidate builds and
attempts. Only authoritative terminal builds whose attempts are all terminal
are eligible. Any retry ancestry/decision or uncertain effect holds the whole
build. A durable `build_retention` row freezes new attempts, retries and output
publication through database triggers. API evidence reads return explicit
`evidence_expired` as soon as selection commits. Status/identity remain readable.

The state machine is `selected → deleting → expired`; unexpected identity or
database/filesystem disagreement changes the row to `held`, never success.
The manifest records each owned path's Linux device, mount, inode, birth time
and type. File size/change timestamps protect file content. Ancestors are
descriptor-opened with no-follow semantics. Symlinks are unlinked as entries,
never followed. Cross-mount descendants and multiply linked regular files
refuse. No recursive pathname delete operation is used.

The manifest has a child-before-parent entry list and a durable cursor. Before
unlinking entry N, persist `pending_delete=true`; rename it without replacement
to a private nonce quarantine name, recheck identity, unlink and fsync its parent,
then commit cursor N+1. A missing entry is acceptable only for that recorded pending deletion
when recovering an interruption. Unexpected absence, replacement or new content
holds the build. Directories must be empty before removal, so unmanifested
entries cannot be silently destroyed. The state-root and ancestor identities
are checked again on every resumed sweep. A try-advisory-lock makes overlapping
sweeps refuse promptly. Restore-epoch binding rejects a stale deletion journal.

Age, count and logical-byte policies operate on reclaimable terminal evidence.
Newest eligible builds are kept until count/size limits require oldest-first
selection; age expiry independently selects old eligible builds. Held/active
and retry/reconciliation data are excluded from reclaimable targets, never
deleted to meet a quota. Logical bytes are file lengths plus UTF-8 log/diagnostic
and source-envelope bytes; they are not physical PostgreSQL relation shrinkage or disk
block availability. PostgreSQL vacuum/reuse is a separate operator concern.

Planning and execution have explicit limits: candidates, aggregate manifest entries,
individual deletion operations, SQL timeout and elapsed time. Histories/trees
that exceed planning limits refuse before selection rather than making an
unbounded scan or partially guessing ownership. The eventual-recovery guarantee
applies to the declared bounded workload, with no new writes to selected data.
Repeated sweeps resume deletion until all selected rows expire. Append-only
audit/event truth remains outside the reclaimed payload budget; this is not
unlimited history compaction.
