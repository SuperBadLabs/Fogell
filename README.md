# Fogell

Self-hosted CI in F# for human and AI development feedback loops.

Fogell runs versioned native JSON pipelines on a trusted Linux worker. The
controller provides authenticated, idempotent submission, bounded progressive
feedback, structured failure diagnostics, source snapshots, artifacts and
cancellation. PostgreSQL owns execution authority; uncertain execution requires
reconciliation instead of automatic replay.

## A pipeline

Save as `pipeline.json`:

```json
{
  "version": 1,
  "stages": [
    {
      "name": "verify",
      "steps": [
        {"run": "dotnet build -c Release", "timeout_seconds": 300},
        {"run": "dotnet tests/Fogell.Domain.Tests/bin/Release/net10.0/Fogell.Domain.Tests.dll", "timeout_seconds": 300}
      ]
    }
  ]
}
```

See the [pipeline contract](docs/PIPELINES.md),
[feedback client](docs/runbooks/feedback-client.md), and
[controller operations](docs/runbooks/controller-host.md).

## Build and test

Install the SDK pinned in `global.json`, Linux `setsid`, and PostgreSQL 16
(or Podman to provision a disposable test instance):

```bash
./scripts/pg-test-db.sh fogell-development-tests
# Set FOGELL_TEST_DATABASE_URL to the disposable database URL printed above.
./scripts/build-and-test.sh
```

The gate restores locked dependencies, builds the solution, runs every test
project, and exercises native pipeline execution and journal recovery. Database
suites must actually run; an unavailable database cannot produce a passing gate.
CI additionally exercises a real controller and worker against disposable state.

## Boundaries

The supported deployment is one mutually trusted execution domain on a dedicated
Linux host/VM, with one local worker. The API bearer is global operator authority.
Shell commands share the service OS identity; external resource and network
controls are required. See the [threat model](docs/THREAT_MODEL.md).

This authoring release is a breaking change: existing non-JSON definitions must
be converted and deliberately resubmitted with new idempotency keys. Drain the
old controller before upgrading, keep paired database/state backups, and use
clean release directories. Historical campaigns for earlier runtimes do not
qualify this native runtime. See [upgrade and recovery](docs/runbooks/recovery.md).

## Layout

- `src/Fogell.Pipeline.Parser`: bounded native JSON admission.
- `src/Fogell.Runtime`: native stage/step orchestration.
- `src/Fogell.Execution`: process containment, output protection and publishing.
- `src/Fogell.Controller.*`: HTTP API and local worker supervision.
- `src/Fogell.Store`, `src/Fogell.Journal`: durable authority and execution records.
- `tools/`: client, run host, retention and restore activation.
- `tests/`, `scripts/`: behavioral tests and operational checks.

[Product roadmap](docs/PRODUCT_DIRECTION.md). Licensed under Apache 2.0.
