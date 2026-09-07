# Runbook — the no-egress fence and the corpus lane

Built by FG-244 (2026-09-04). Read the ticket for why each side is shaped
as it is; this page is how to run it.

## Run a corpus file on both engines under the fence

```bash
scripts/run-corpus-differential.sh /sn8100/work/exchange/crucible-gate/corpus/jenkinsfiles/<stem>.Jenkinsfile
```

The lane archives `HEAD` into a private directory before consuming repository
policy. The executing runner must be byte-identical to its archived copy; the
corpus verifier and manifest, allowlist, runtime pins and checker, workspace
helper, no-egress fence, and CLI sources are then consumed only from that
private committed tree. It restores the CLI in locked mode, builds it into a
private output, hashes the complete output closure, and requires the CLI's
runtime-guard capability handshake. It then applies and PROVES the Jenkins fence,
proves the Fogell fence from inside the run's own scope, runs the
differential into a private directory, and removes the Jenkins fence on
exit. Receipts are promoted into `differential/receipts/` under the corpus
stem only when the run completed under a fence that stood throughout, and
nothing already in that directory is ever modified by an aborted run;
regenerate the ledger with
`FOGELL_CORPUS=/sn8100/work/exchange/crucible-gate/corpus scripts/bin/generate-scorecard`.

An allowlist row may name a fourth-field runtime-pin ID from
`differential/corpus-runtime-pins.tsv`. Under the cross-host lease and before
any corpus execution, each pin declares whether its command must be `present`
or `absent` under Fogell's exact compatibility `PATH`. A `present` row carries
absolute local and Jenkins paths plus the common file SHA-256. An `absent` row
must carry `-` for both paths and the SHA-256; the checker then proves that
`command -v` resolves nowhere locally and in the actual disposable Jenkins
container identity. `composer-absent-v1` is the absence prerequisite for the
Tier-1 Composer case. SSH, podman, shell, or malformed-probe failures are not
accepted as absence. The checker runs once before execution and again before
receipt promotion.

The lane also injects that typed requirement, the fixed `PATH`, and a
cryptographic per-trigger ownership token as explicit Jenkins build
parameters. The trigger's returned queue item, the build API's `queueId`, both
parameters, and the exact Replay definition must all agree. The corpus
definition is augmented with a first stage that checks the token, effective
PATH, and required command presence or absence before emitting one case- and
nonce-bound marker; only that exact marker is removed from comparison. Separate
real Pipeline guard builds apply the same requirement immediately before and
after the corpus history, and every allocation must report the pinned node.

The lane never executes on the long-lived `jenkins-lab` JVM or its mutable
home. Under the cross-host lease it installs a token-bound Luigi access fence
before any listener, starts a random-named controller with `--pull=never` from
the exact image ID, and captures the returned full container ID. Jenkins gets
one fresh anonymous home volume; readiness is accepted only with the pinned
core, exact 154-plugin closure, zero jobs, empty queue, idle executor set, exact
image/digest/runtime-requirement/port tuple, and one online node. The namespace egress fence
is installed as soon as the new container has a PID. Every collector uses the
full random container ID. The lane carries all Jenkins REST traffic over a
life-bound SSH local forward; an exact owner-UID nftables rule exists on HeMan
before that TCP listener, while Luigi permits the authenticated SSH uid and
rejects every other local uid and lab-network client. All three rulesets and
the controller start instant are heartbeated. On exit the tunnel dies first,
then the exact token-labeled container and its fresh home volume are removed
and proven absent before either access fence is removed. Any missing,
malformed, unavailable, or changed value discards the private receipts and
leaves the relevant fence standing if cleanup cannot be proved. Rows with no fourth field keep the
historical no-tool behavior. A runtime-backed invocation is one case and one
pin so the two guard builds unambiguously bracket its build. The runner names that committed pin file
literally; there is no caller override for the expected tuple.

It refuses a file that is not under the pinned corpus, a corpus that does
not verify, a file whose sha256 and stem are not on
`differential/corpus-allowlist.tsv` (read the file, record its executed
surface there in one line, then run — the corpus is untrusted and the list
is the permission), a missing or mismatching runtime pin selected by that row,
an invalid resolution expectation, an absent command that resolves on either
host, or a present command with a path or byte mismatch,
a runner different from HEAD, a private archive/restore/build/capability
failure, a second lane of this user on this host (a lock in
`$XDG_RUNTIME_DIR`), an oracle with busy executors, a lane lease it
cannot take (a `flock` on `~/.fogell-corpus-lane.lock` ON THE JENKINS HOST,
held for exactly as long as the lane's pid exists — one corpus lane at a
time across every user and host), a stale disposable oracle, and a fence it cannot prove. A refusal
before the differential runs has executed nothing. On exit it kills every
process the run left in its scope, kills the SSH tunnel, removes the exact
token-labeled disposable container with `podman rm -fv`, proves both its ID and
home-volume ID absent, and only then removes the access fences. If the lane itself is killed, the fenced run notices
the lane's pid is gone and tears itself down. Any unconfirmed oracle, volume,
tunnel, ruleset, nft/jq inspection, or post-delete result leaves the relevant
access fence UP and names the recovery path below. A run that loses its fence (the container
restarted, the table gone) or its lease is aborted and its receipts are
reverted; rerun it.

The v7 runtime-guard capability also threads the same typed requirement into
Fogell. Every retained build re-resolves it against Fogell's fixed build
environment immediately before that build enters the engine and against the
exact effective environment handed to every shell launch. A mismatch is a
harness failure outside the comparison path, so it cannot become a matching
failure trace or a sealable receipt.

## What "proven" means

`scripts/no-egress-fence.sh jenkins verify` runs inside the container:
`getent` must fail, `curl` to a name, a public IP and a LAN IP must fail
(the rule passes only the reply direction of established flows, so even a
connection a leftover process opened before the fence cannot carry data)
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

The disposable Jenkins controller plus both owner-UID access fences are the
oracle boundary. The Fogell side is not: it fences a
cgroup of the operator's own UID, and that UID can hop off it by
`ssh <host> curl …`, by `systemd-run --user`, by passwordless sudo, or via
any loopback listener (all measured, FG-244). It stops accidental egress
from the executed surface; the allowlist-by-reading rule is what stands
between a hostile corpus file and the operator's account.

## Requirements

- luigi: rootless podman, `nft`, `nsenter`, `jq`, `curl`, and passwordless
  `sudo -n nft` plus `sudo -n -u nobody curl`. The pinned image must already be
  local; `--pull=never` forbids registry fallback.
- HeMan: `systemd-run`, `jq`, `curl`, `nft`, and passwordless `sudo` for the
  Fogell cgroup rule and local tunnel owner-UID rule.
- The oracle host must be in `/etc/hosts` (it is): inside the scope names do
  not resolve.

## Inspect and clean up

```bash
scripts/no-egress-fence.sh jenkins status   # PRESENT/ABSENT plus a live egress probe (a PRESENT fence with no lane running is a leftover: recover below — `apply` and the lane refuse to replace it)
scripts/no-egress-fence.sh fogell status    # any fogell_fence_* table (a live run, or a stale one)
ssh luigi 'sudo -n nft list table inet fogell_jenkins_access; podman ps -a --filter label=fogell.lane-token --no-trunc'
# Verify the label/token/full ID and home-volume identity, then:
ssh luigi 'podman rm -fv <full-container-id>; podman container exists <full-container-id>; podman volume exists <home-volume-id>'
ssh luigi 'sudo -n nft delete table inet fogell_jenkins_access' # only after both exists commands return 1
sudo nft list table inet fogell_jenkins_tunnel_access
sudo nft delete table inet fogell_jenkins_tunnel_access        # only after the SSH listener is absent
ssh <host> 'flock -n ~/.fogell-corpus-lane.lock -c true || pkill -f "flock -n .*fogell-corpus-lane.lock"'   # a stuck lease (should not happen: the holder dies with the lane's pid)
```

The namespace egress ruleset vanishes with the disposable container, but both
host access tables persist until verified cleanup. The Fogell rule is
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
