# Native pipeline contract, version 1

A definition is one UTF-8 JSON object with `version: 1`, optional `env`, and a
nonempty `stages` array. A stage has a unique `name` and nonempty `steps` array.
Names cannot contain control characters and are limited to 128 characters.
Unknown fields, duplicate object keys, unsupported versions and malformed values
are rejected before a build or idempotency key is admitted. There is no embedded
expression language, implicit translation or plugin dispatch.

Every step has exactly one operation:

| Field | Value and behavior |
| --- | --- |
| `run` | Nonempty shell command, executed in a fresh process. Shell expansion follows the selected shell. A leading shebang selects its interpreter; otherwise `/bin/sh` runs with `-xe`. |
| `echo` | Literal text, including an empty string. No interpolation. |
| `archive` | Nonempty comma-separated artifact glob patterns. Publishes bounded files outside the workspace; no matches fail. |
| `test_report` | Nonempty comma-separated JUnit XML glob patterns. Failed cases produce structured diagnostics and an `unstable` build. Missing/invalid reports fail. |

Optional step fields:

| Field | Contract |
| --- | --- |
| `timeout_seconds` | Integer 1–86400, default 600. Applies to shell execution and publishing. |
| `env` | Object of literal string values. Overrides pipeline `env`. Names follow `[A-Za-z_][A-Za-z0-9_]*`; `FOGELL_` names are reserved. |
| `working_directory` | Existing relative directory under the workspace, default `.`. Absolute paths, traversal and symlink components are refused. |
| `always` | Boolean, default false. Runs after an earlier workload failure/timeout; it cannot guarantee execution after process death or infrastructure failure. |

Stages and steps run sequentially. Failure or abort skips subsequent ordinary
steps. Unstable test results allow further steps; later success never erases a
worse result. `always` steps have normal durable step identities. Commands share
workspace files, but each process gets a new environment. A shell `export` does
not change later steps. The baseline provides PATH and build-local HOME/TMPDIR,
plus WORKSPACE and BUILD_NUMBER. It does not inherit controller secrets.

Limits: 256 KiB definition, depth 16, 16,384 JSON values, 4,096 entries per
collection, 16 KiB per string/key. The controller can impose a lower source byte
limit. Whole-build output is limited to 32 MiB UTF-8 and 100,000 records;
individual process buffers and artifact publication have independent limits.

```json
{
  "version": 1,
  "env": {"CONFIGURATION": "Release"},
  "stages": [{
    "name": "test",
    "steps": [
      {"run": "./build-and-report.sh", "timeout_seconds": 300},
      {"test_report": "reports/*.xml", "always": true},
      {"archive": "reports/*.xml", "always": true}
    ]
  }]
}
```

Source snapshots retain the exact definition and explicit source files. They
establish input identity, not a hermetic toolchain. See [client usage](runbooks/feedback-client.md).
A journaled completed step is not repeated. A started step without a durable
outcome requires reconciliation. Shell side effects do not have an exactly-once
guarantee. Missing output publication leaves execution nonterminal.
