# FG-175 oracle probes

Direct Jenkins oracle measurements from the pinned Jenkins 2.568.1 lab on
2026-08-22 UTC. Every Jenkinsfile has an `early` stage that would create
`early.txt` if execution began. Each corresponding raw log records the case,
job and build identifiers, Jenkins version, terminal build JSON, complete
console, and a fail-closed workspace status collected from the lab container.

| Case | Jenkins result | Compile diagnostic | Workspace files |
|---|---|---|---|
| `when-duplicate-tag.Jenkinsfile` | `FAILURE` | duplicate `pattern` | absent |
| `when-duplicate-change-request.Jenkinsfile` | `FAILURE` | duplicate `target` | absent |
| `when-duplicate-nested.Jenkinsfile` | `FAILURE` | duplicate `value` | absent |
| `when-duplicate-parenthesised.Jenkinsfile` | `FAILURE` | duplicate `target` | absent |
| `when-invalid-changeset-glob.Jenkinsfile` | `FAILURE` | invalid `glob`; expected `pattern` | absent |
| `when-invalid-directive-value.Jenkinsfile` | `FAILURE` | `beforeAgent` requires a boolean | absent |

The logs show that Jenkins reached Groovy/Declarative compilation and never
started the Pipeline: every result is `FAILURE`, every console names the
compile-time rule, and every `WORKSPACE:` section records `STATUS: absent`.

## Reproduction

The manifest-bound runner and case files were executed directly from this
directory in the HeMan worktree. The invocation shape was:

```text
bash evidence/20260822T023145Z-fg-175-probes/jenkins-probe.sh \
  evidence/20260822T023145Z-fg-175-probes/<case>.Jenkinsfile \
  fg175-<case> > /tmp/fg175-probes/<case>.jenkins.log
```

It creates a sandboxed Pipeline job through Jenkins HTTP, starts build 1,
requires `/api/json` to reach terminal state, downloads `/consoleText`, and
asks the Luigi lab host to report the workspace as present or absent and list
any files. A collector/SSH/container failure terminates the probe instead of
being printed as an empty workspace. Each log records the submitted case's
SHA-256; all six equal the corresponding manifest-bound Jenkinsfile digest.
The job is then deleted.
`MANIFEST.sha256` binds the cases, raw logs, this README, and the repository
base used for the work.

The ordinary differential harness cannot seal compile-shaped refusals as
tier-1 receipts because Fogell rejects them during admission and deliberately
produces no build trace. `scripts/prove-section-refusals.sh` holds Fogell's
corresponding property: named refusal plus absence of `early.txt`, paired with
valid controls that still execute.
