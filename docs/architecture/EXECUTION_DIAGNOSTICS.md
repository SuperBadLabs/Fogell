# Structured execution diagnostics (FG-266)

The runner already exposes masked progressive text, step start/finish hooks,
terminal build results, and executor exit codes. JUnit ingestion traverses actual
report elements and returns counts/duration; it previously discarded individual
case identity. The implementation collects failed cases during that existing
parse. It never reconstructs a failure by searching log text or reparsing files.

A diagnostic has schema version 1 and category `workload_step`, `test`, or
`infrastructure`. Fields include stage/step, result, nullable exit code,
nullable test name/class, bounded message/output, nullable source path/line/column,
report path, published artifact references, and explicit truncation. Pipeline
positions come from the parser for ordinary steps. Hosted Groovy locations remain
null because their positions are not absolute Jenkinsfile positions. JUnit source
locations are report-supplied data, not independently verified source mappings.
Report paths do not imply that a report was archived: artifact references remain
empty unless publication is known.

The run-wide secret registry masks complete fields before clipping; diagnostics
also pass credential leak screening. Direct executor callbacks receive masked,
bounded values too. With a live registry, its shared registration lock covers
masking, clipping and synchronous publication so a concurrent binding cannot
turn into an unrecognizable clipped secret prefix. A shared 2,000-byte UTF-8 field budget
bounds JSON escaping below a 16 KiB frame. Up to 64 failed cases are retained per
JUnit invocation, followed by an explicit omission diagnostic when necessary.
Counts remain complete. Serialized diagnostics consume the same run-wide output
character budget as logs and captured output.

Run.Host writes `D1:` followed by base64 JSON as a distinct event-file frame.
Ordinary user output always takes the base64-only log path, so diagnostic-shaped
stdout does not create typed evidence. The worker uses the existing fenced,
restore-epoch-aware atomic log batch append; a nullable `diagnostic` JSONB column
keeps evidence and the log cursor in one durability unit. Empty console body on a
diagnostic frame avoids copying its structured data into raw logs. Each diagnostic
has an immutable public identity formed from its attempt UUID and attempt-local
sequence. Repeated reads or reopening the store preserve that identity; replaying
an already-admitted batch cannot add a second record. A genuinely executed step
retry is a new occurrence and receives a new sequence.

Feedback retains schema version 1 with additive `diagnostic` and `diagnostic_id`
chunk properties. Null identifies ordinary console chunks. The existing 64 KiB
page budget counts both log body and serialized diagnostic bytes, and all chunks
share the same continuation cursor and PostgreSQL statement snapshot. Retention
expires both together. An agent can identify the failed test from the diagnostic
without scanning the console.

Owned controller reconciliation publishes an infrastructure diagnostic in the
same fenced transaction as its status transition. Runner-level refusals publish
a fixed, bounded infrastructure reason without forwarding arbitrary exception
text. Expired authority cannot append evidence; recovery classifications remain
controller authority and are never treated as workload success.

Migration `0015_execution_diagnostics.sql` adds a nullable JSONB column and encoded
size constraint. Apply it before the new writer; old rows remain valid. Existing
binaries ignore the column on rollback, retaining evidence but not exposing it.
Do not drop the column to roll back. Operational disaster recovery must restore
the matching database and filesystem pair; this additive migration does not
change that requirement.

Validation includes planted shell/test failures, source metadata, secret-bearing
and oversized test diagnostics, explicit failed-case omission, protocol spoofing,
stale writer refusal, repeat publication, reopened-store identity, and atomic
infrastructure evidence. Live M1 replay and diagnostic observation timings are
recorded with the combined campaign, not inferred from unit-test duration.
