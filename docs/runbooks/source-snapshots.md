# Explicit source snapshots (FG-267)

A snapshot submission binds the exact selected file bytes, executable flags,
pipeline bytes, Linux architecture/.NET major, a parent correction-loop UUID,
and an optional expected runner-tool fingerprint. It makes no Git commit or
clean-tree claim. Working-tree edits and moving branches cannot relabel a prior
snapshot; its identity is its content digest. Unknown metadata, including caller
`revision`, `branch`, `clean`, or `token` fields, is refused.

The controller never checks out a caller URL. Snapshot creation is offline and
requires an explicit newline-separated inventory of relative paths. It does not
collect Git metadata, authentication tokens, process environment or arbitrary
unlisted files. Hidden paths, credential filenames/key extensions, symlinks,
traversal, duplicate/colliding paths, and `bin`/`obj`/`evidence` are refused.
The inventory selects source contents intentionally; this is not secret scanning
of arbitrary application source. Bundle output is created privately (0600) and
must not already exist.

Build the client and Run.Host, then capture the installed runner identity:

```bash
dotnet tools/Fogell.Client/bin/Release/net10.0/Fogell.Client.dll tool-identity \
  --run-host tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host
```

`run_host_sha256` hashes sorted filenames and content hashes for the configured
entry executable plus its top-level `.dll`, `.so`, `.deps.json` and
`.runtimeconfig.json` files. It changes when the managed engine changes.
`observed_runtime_version` records the .NET runtime running the client/worker.
System tools, kernel and remote services are not captured by that fingerprint;
a reproducibility campaign must declare and preserve those external conditions.

Prepare an inventory such as `src/input.txt` and `scripts/test.sh`, one path per
line, then pack it with the tool fingerprint:

```bash
dotnet tools/Fogell.Client/bin/Release/net10.0/Fogell.Client.dll snapshot \
  --pipeline ./Jenkinsfile --source-root ./candidate --files-from ./inventory.txt \
  --output /tmp/candidate.snapshot.json --parent-loop "$FOGELL_LOOP_UUID" \
  --tool-sha256 "$FOGELL_TOOL_SHA256"
```

`--tool-sha256` is optional: omitting it leaves the requested tool unpinned, while
feedback still records the observed tool identity. Use it for reproduced runs.
`parent-loop` must be a nonempty UUID. The fixed environment contract is Linux,
`x64`/`arm64`, and .NET major 10, matched against the controller before launch.
Transport is plain versioned JSON with Base64 file/pipeline bytes; no archive
extraction or decompression occurs. Limits are 4096 files, 16 MiB aggregate
source-plus-pipeline bytes, 24 MiB envelope, and 512 characters per relative path.
The controller's existing pipeline limit still applies to the decoded pipeline.

Submit it through the usual authenticated client:

```bash
dotnet tools/Fogell.Client/bin/Release/net10.0/Fogell.Client.dll submit \
  --url "$FOGELL_URL" --organization "$FOGELL_ORG" --project "$FOGELL_PROJECT" \
  --token-file "$FOGELL_TOKEN_FILE" --snapshot /tmp/candidate.snapshot.json \
  --idempotency-key candidate-001
```

Choose exactly one of `--snapshot` and the legacy `--pipeline`. The request uses
`application/vnd.fogell.submission.v1+json`. Existing idempotency binds the full
raw envelope bytes and controller placement policy: identical replay returns the
same build/attempt; changed source, pipeline, parent, environment, tool pin or
manifest bytes conflict. A new intentional candidate uses a new key. Expired
build evidence returns 410 `evidence_expired`, never new execution under an old key.

Ordinary feedback includes a bounded `source_identity` object, never file contents:

```json
{
  "schema_version": 1,
  "kind": "content_snapshot",
  "verification_state": "verified",
  "snapshot_sha256": "...",
  "pipeline_sha256": "...",
  "manifest_sha256": "...",
  "parent_loop_id": "...",
  "expected_tool_sha256": "...",
  "file_count": 2,
  "environment": {"os": "linux", "architecture": "x64", "dotnet_major": 10},
  "run_host_sha256": "...",
  "observed_runtime_version": "...",
  "attempt_id": "...",
  "fence": 1
}
```

`pending` means only admission has occurred. `verified` means the active fenced
worker validated immutable snapshot bytes, environment and any requested tool pin.
Run.Host then verifies and installs those bytes after its fresh-workspace wipe,
before journal execution metadata or pipeline steps. A materialization mismatch
refuses execution; successful snapshot watches require verified identity. The
workload can subsequently edit its workspace as part of execution. This records
initial execution inputs, not a claim that arbitrary build commands are hermetic.

The explicit authenticated `source` command retrieves the original envelope for
a second fresh submission. Unlike other commands it emits only the envelope,
without adding `client_elapsed_ms`, so the download can be resubmitted directly:

```bash
dotnet tools/Fogell.Client/bin/Release/net10.0/Fogell.Client.dll source \
  --url "$FOGELL_URL" --organization "$FOGELL_ORG" --project "$FOGELL_PROJECT" \
  --token-file "$FOGELL_TOKEN_FILE" --build "$FOGELL_BUILD" \
  --max-response-bytes 25165824 > /tmp/reproduce.snapshot.json
```

The source endpoint requires matching organization/project lineage; legacy
pipeline-only builds have no downloadable snapshot. Retention expires payloads
explicitly. Source identity metadata remains separate from expirable bytes.

Migration 0016 creates tenant-isolated, bounded verification metadata. Existing
raw Jenkinsfile submissions remain valid. Before rolling back to a release that
does not decode envelopes, drain/hold all admitted snapshot attempts and stop
snapshot admission; an older worker cannot execute these definition bytes.
Do not delete provenance during rollback. Apply the paired-state recovery and
retention gates before a sustained production rollout.
