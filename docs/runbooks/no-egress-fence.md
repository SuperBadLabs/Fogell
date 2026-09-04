# Runbook — the no-egress fence and the corpus lane

Built by FG-244 (2026-09-04). Read the ticket for why each side is shaped
as it is; this page is how to run it.

## Run a corpus file on both engines under the fence

```bash
dotnet build tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release
scripts/run-corpus-differential.sh /sn8100/work/exchange/crucible-gate/corpus/jenkinsfiles/<stem>.Jenkinsfile
```

The lane verifies the pinned manifest, applies and PROVES the Jenkins fence,
proves the Fogell fence from inside the run's own scope, runs the
differential, and removes the Jenkins fence on exit. Receipts land in
`differential/receipts/` under the corpus stem; regenerate the ledger with
`FOGELL_CORPUS=/sn8100/work/exchange/crucible-gate/corpus scripts/bin/generate-scorecard`.

It refuses a file that is not under the pinned corpus, a corpus that does
not verify, a file whose sha256 and stem are not on
`differential/corpus-allowlist.tsv` (read the file, record its executed
surface there in one line, then run — the corpus is untrusted and the list
is the permission), a missing CLI build, a second lane of this user on this host (a
lock in `$XDG_RUNTIME_DIR`), an oracle with busy executors, a lane lease it
cannot take (a `flock` on `~/.fogell-corpus-lane.lock` ON THE JENKINS HOST,
held for exactly as long as the lane's pid exists — one corpus lane at a
time across every user and host), and a fence it cannot prove. A refusal
before the differential runs has executed nothing. On exit it kills every
process the run left in its scope, quiesces the container (kills
everything that is not init, the JVM that runs `jenkins.war`, or the exec),
and removes the Jenkins fence, reporting loudly if the removal fails or
cannot be confirmed. If the lane itself is killed, the fenced run notices
the lane's pid is gone and tears itself down; the Jenkins fence stays
(see below).

## What "proven" means

`scripts/no-egress-fence.sh jenkins verify` runs inside the container:
`getent` must fail, `curl` to a name, a public IP and a LAN IP must fail
(and the IP refusal must take under two seconds — a DROP would hang), the
container must still answer itself on loopback, HeMan must still reach the
oracle port, and the reject counter must be live. `fogell run` does the same
from inside the scope (`getent` included), plus `ssh <host> true` for the
workspace collector. Any surprise refuses, and a probe that produced no
output or no tool is a failure, not a refusal. Probe targets: `FOGELL_FENCE_PROBE_HOST`
(example.com), `FOGELL_FENCE_PROBE_IP` (1.1.1.1), `FOGELL_FENCE_PROBE_LAN_IP`
(unset by default; the FG-244 landing run set it to the router, which adds an
eighth check to the seven each side runs).

## What it is not

The Jenkins side is a network boundary. The Fogell side is not: it fences a
cgroup of the operator's own UID, and that UID can hop off it by
`ssh <host> curl …`, by `systemd-run --user`, by passwordless sudo, or via
any loopback listener (all measured, FG-244). It stops accidental egress
from the executed surface; the allowlist-by-reading rule is what stands
between a hostile corpus file and the operator's account.

## Requirements

- luigi: rootless podman, `nft` and `nsenter` on the host (present), the
  container running. No root. Nothing about the container is changed.
- HeMan: `systemd-run`, `nft`, and passwordless `sudo` for the one rule.
- The oracle host must be in `/etc/hosts` (it is): inside the scope names do
  not resolve.

## Inspect and clean up

```bash
scripts/no-egress-fence.sh jenkins status   # PRESENT/ABSENT plus a live egress probe
scripts/no-egress-fence.sh fogell status    # any fogell_fence_* table (a live run, or a stale one)
scripts/no-egress-fence.sh jenkins quiesce  # if a lane died before its trap ran: FIRST kill what it left …
scripts/no-egress-fence.sh jenkins remove   # … THEN open the network again
ssh <host> 'flock -n ~/.fogell-corpus-lane.lock -c true || pkill -f "flock -n .*fogell-corpus-lane.lock"'   # a stuck lease (should not happen: the holder dies with the lane's pid)
```

The Jenkins ruleset vanishes on container restart. The Fogell rule is
deleted by the run's exit trap after the scope is killed; a stale one
(its scope cgroup gone) is swept by the next `fogell run`, and a live one
belonging to another run is left alone.

## Do not

- Do not run the hand-written lane (`scripts/run-differential.sh`) while
  the Jenkins fence is applied: its git-step cases reach the SCM daemon on
  the lab host and would be refused. The corpus lane removes the fence on
  exit for exactly this reason.
- Do not kill a lane by pattern. Two lanes on one box share a script name;
  kill by PID.
