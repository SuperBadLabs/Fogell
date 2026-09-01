#!/usr/bin/env bash
# FG-000/FG-001 — the gate every ticket must pass before its PR.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 1
echo "=== sdk ==="; dotnet --version
./scripts/prove-dependency-locks.sh \
  || { echo "DEPENDENCY-LOCK/SOURCE-CLEARED BUILD PROOF FAILED"; exit 1; }
echo "=== tests ==="
# FG-229. Prove the tracked-project inventory and Expecto-summary boundary
# before trusting them. The old `tests/<dir>/<dir>.fsproj` convention silently
# skipped nested and differently named projects while still producing a green
# gate. The shared runner is also used by evidence sealing, so those two
# publication boundaries cannot drift back to different test populations.
./scripts/prove-project-tests.sh \
  || { echo "PROJECT TEST INVENTORY PROOF FAILED"; exit 1; }
./scripts/run-project-tests.sh \
  || { echo "TESTS FAILED"; exit 1; }

# FG-228. Stash containment depends on both refusing linked-directory prefixes
# and copying only from a descriptor whose physical identity is the selected
# lexical workspace path. Recreate the old two-part traversal mechanism in a
# scratch candidate, require it to compile, and prove the named production tests
# reject the external-byte copy.
echo "=== stash symlink containment mutation proof (FG-228, blocking) ==="
./scripts/prove-fg228-stash-symlinks.sh \
  || { echo "STASH SYMLINK CONTAINMENT PROOF FAILED"; exit 1; }
./scripts/check-fg228-evidence.sh \
  || { echo "STASH SYMLINK EVIDENCE CHECK FAILED"; exit 1; }

# FG-026. The ordinary Store project run above covers the ledger and already
# refuses a no-summary database skip. This focused wrapper additionally proves
# that the named ten-test slice and exact live schema marker ran, after first
# rejecting planted skip/count/marker/summary/exit-code outputs.
echo "=== effect-checkpoint ledger proof (FG-026, blocking) ==="
./scripts/prove-fg026-effect-ledger.sh \
  || { echo "EFFECT-CHECKPOINT LEDGER PROOF FAILED"; exit 1; }

# FG-207. StepFinished and its optional StepReason are historical records but
# one current durability group: exact order under one lock and exactly one
# EveryStep Flush(true). The deterministic observer proof runs everywhere;
# strace adds a syscall-level count only on hosts that provide it.
echo "=== grouped step-finish force proof (FG-207, blocking) ==="
./scripts/prove-fg207-fsync.sh \
  || { echo "GROUPED STEP-FINISH FORCE PROOF FAILED"; exit 1; }

# FG-104. BLOCKING. Every MEASURED claim must cite a receipt or admit UNPROVEN. The
# backlog it was introduced against (30) is zero, so the check now fails the build instead
# of printing at it — an advisory check nobody must act on decays into noise.
# FG-226. The audit tools are compiled from scripts/fsx/*.fsx, never committed:
# an fflat build is not reproducible, so a committed binary could not be proven
# to match its source. Building them HERE makes that link true by construction
# for the very run that then trusts them.
./scripts/build-audits.sh || { echo "AUDIT TOOL BUILD FAILED"; exit 1; }
# FG-226. Review found three error paths a success-only port comparison could
# not see: a stale fflat version broke discovery, malformed crumb JSON silently
# removed authentication, and failed setup POSTs were ignored. Prove those
# known-bad environments before trusting the native tools.
./scripts/prove-fg226-audit-tools.sh || { echo "FG-226 AUDIT TOOL PROOF FAILED"; exit 1; }
# The prelude is shared by all eight tools, so a divergence in it changes every
# audit's verdict at once. Proven before any of them runs.
./scripts/prove-fsx-prelude.sh || { echo "FSX PRELUDE PROOF FAILED"; exit 1; }

if [ -x scripts/bin/audit-claims ]; then
  echo "=== claim audit + its citation proof (FG-104/FG-174, blocking) ==="
  # THE PROOF RUNS FIRST, for the same reason the stale-ref proof does below. The audit
  # gained a SECOND check — every receipt a comment NAMES must exist — and a checker
  # nobody has watched fail is itself a claim. 14 arms in both directions: six planted
  # dangling citations it must reject, and the real spellings (backticked, colon,
  # multi-build `.b1`, glob, `.receipt.txt`, and prose that merely says "receipt") it
  # must not. Both directions earned their place — the first draft called six genuine
  # citations dangling, and its ACCEPT arms were passing on zero scanned files.
  ./scripts/prove-claim-citations.sh || { echo "CLAIM-CITATION PROOF FAILED"; exit 1; }
  # No `| head`: piping into head can SIGPIPE the audit and mask its exit status, which
  # would make an advisory check silently become a broken one.
  # Status captured explicitly: without it an audit binary that cannot start is indistinguishable
  # from a clean audit, and the planned `--strict` flip would never have failed a build.
  if ! audit_out="$(./scripts/bin/audit-claims --strict 2>&1)"; then
    echo "CLAIM AUDIT FAILED"; printf '%s\n' "$audit_out"; exit 1
  fi
  printf '%s\n' "$audit_out" | sed -n '1,3p'

fi

# FG-104b: a comment naming a mechanism the code no longer has. Three of those
# landed in one day and every one was caught by a reviewer rather than a check —
# `audit-claims` asks a different question (does a MEASURED claim name a
# receipt) that a stale identifier passes trivially.
#
# Scope, stated exactly because vaguer versions of this sentence have been a
# finding TWICE: F# BINDINGS of four or more characters — let/member/type/
# override/default/and declarations and PascalCase record fields — named in comments
# or nested `(* ... *)` blocks. Short names are deliberately out (`x`, `id`,
# `ctx` occur in ordinary English and a checker that fires on prose gets
# switched off), and non-F# symbols are not extracted at all: a deleted shell
# function or shell def can leave a stale comment this audit will not see.
#
# Proven to fail before being trusted, and the proof runs first: 16 binding
# forms, a comment repeating the keyword, a record field on the brace line and
# one named inside a block comment, a string that merely looks like a
# definition, two false-positive cases, and four of the checker's own failure
# modes.
echo "=== stale-reference audit + its own proof (FG-104b, blocking) ==="
# the proof runs FIRST and in scratch repositories: a checker nobody has watched
# fail is a claim, and this one has twice been wrong about its own job
./scripts/prove-stale-refs.sh || { echo "STALE-REF PROOF FAILED"; exit 1; }

# FG-152. Every section Fogell ACTS ON must refuse when it does not parse, and every
# control must still run. The same fix landed on one of two sibling fallbacks three
# times before this proof existed to notice.
./scripts/prove-section-refusals.sh || { echo "SECTION-REFUSAL PROOF FAILED"; exit 1; }

# FG-072. The interpreter sandbox is a load-bearing security boundary, not a
# unit-test implementation detail. Exercise every name in Sandbox.knownEscapes
# through the real parser/interpreter/host path, exercise generic default-deny
# fallbacks, prove sanctioned calls still work, and prove the checker rejects
# timeout/signal execution states plus nine
# record/workspace mutations: unique non-failure terminal, extra terminal,
# generic-failure, unnamed, missing-boundary-reason, both reason-class crosswires,
# split-record predicate aggregation, and no-halt. The proof binds
# the exact current-worktree net10 Release host; no ambient binary override exists.
echo "=== sandbox-denial proof (FG-072, blocking) ==="
./scripts/prove-sandbox-denials.sh || { echo "SANDBOX-DENIAL PROOF FAILED"; exit 1; }

# FG-222. The host has controller credentials, database configuration and SCM
# transport authority in its environment. A build receives only the fixed system
# PATH plus a run-scoped neutral Fogell HOME and explicit pipeline overlays. The proof drives
# GString, shell and a recording Git launcher, then mutates status and content
# artifacts to demonstrate that its checker rejects planted regressions.
echo "=== controller/build environment isolation proof (FG-222, blocking) ==="
./scripts/prove-control-env-isolation.sh \
  || { echo "CONTROLLER/BUILD ENVIRONMENT ISOLATION PROOF FAILED"; exit 1; }

# FG-223. A checksum-valid bundle is not evidence when a prerequisite failed.
# The scratch proof plants failures in corpus verification, build, test exit,
# summary emission and extra-file binding, and first rejects an always-green
# sealer so the proof's own exit-code oracle is observed failing.
echo "=== fail-closed evidence sealer proof (FG-223, blocking) ==="
./scripts/prove-seal-evidence.sh \
  || { echo "FAIL-CLOSED EVIDENCE SEALER PROOF FAILED"; exit 1; }

# FG-037. The live 250/251/400 comparison stays off CI because it needs the
# pinned Jenkins lab. Its fail-closed evidence boundaries are pure: fourteen semantic
# mutations, three controller-identity substitutions, eleven collector/configuration
# attacks, two manifest attacks and three measured-source bundle attacks must all
# be rejected before publication.
echo "=== step-ceiling evidence-boundary proof (FG-037, blocking) ==="
./scripts/prove-fg037-step-ceiling.sh \
  evidence/20260827T185436Z-fg037-step-ceiling \
  || { echo "STEP-CEILING EVIDENCE-BOUNDARY PROOF FAILED"; exit 1; }
bash scripts/check-fg037-source-bundle.sh \
  evidence/20260827T185436Z-fg037-step-ceiling/source/fg037-measured-source.bundle \
  evidence/20260827T185436Z-fg037-step-ceiling/source/allowed_signers \
  refs/heads/codex/fg-037-step-ceiling-publish \
  804bf7967cf3708eb3bb44387d59a24310c89607 \
  488b662000dea32859ae507f92f4dc045f6e8fcd \
  65674f9a4af80e358f645ad3409765a8738c68b4 \
  7e09d220260b9117890cf4275fc240d989101f7c \
  || { echo "STEP-CEILING MEASURED SOURCE BUNDLE FAILED"; exit 1; }
python3 scripts/check-fg037-manifest.py \
  --expected-manifest-sha256 \
  2944f2adde1122ab1d6cfd7cceb911e4b478a643156334ae79a9531fe2205891 \
  evidence/20260827T185436Z-fg037-step-ceiling \
  || { echo "STEP-CEILING EVIDENCE MANIFEST FAILED"; exit 1; }
dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj \
  -c Release --no-build -- \
  --verify-seals evidence/20260827T185436Z-fg037-step-ceiling/receipts \
  || { echo "STEP-CEILING RECEIPT SEALS FAILED"; exit 1; }

# FG-162. Board rows quoting generated counts are re-derived from the committed
# ledger. Runs EVERYWHERE including CI — both files are in the repo, unlike the
# corpus-dependent scorecard check below.
./scripts/bin/audit-board-numbers || { echo "BOARD-NUMBER AUDIT FAILED"; exit 1; }
./scripts/prove-board-numbers.sh || { echo "BOARD-NUMBER PROOF FAILED"; exit 1; }

# FG-198. Queue rows checked against the line-208 rule's deny-list — a FLOOR the
# audit itself states, not the rule. Nineteen manual-sweep failures across two
# shapes argued for a script; the proof runs first, same as every checker here.
./scripts/prove-queue-rows.sh || { echo "QUEUE-ROW PROOF FAILED"; exit 1; }
./scripts/audit-queue-rows.py || { echo "QUEUE-ROW AUDIT FAILED"; exit 1; }

# FG-199. The live guard needs an already-published PR and GitHub metadata, so it
# cannot belong in this pre-publication gate. Its OFFLINE proof does: a fake gh
# records the known-bad #58/#59/#60 heads and plants both pass and refusal arms.
# No credentials or network are used here.
./scripts/prove-review-coverage.sh \
  || { echo "REVIEW-COVERAGE PROOF FAILED"; exit 1; }

# FG-093. The live provenance manifest is created outside the candidate checkout
# and exists only at release time, so the ordinary pre-publication gate cannot
# perform a live release verification. Its OFFLINE proof can and must run here:
# a recursive scratch Git repository proves the exact tuple, initialized gitlink
# identities, filter-free raw stage-0 worktree bytes/modes against index blobs,
# physical-untracked inventory, clean index/config view, five downstream
# bindings, Git-environment removal, and argv-only exec boundary.
# Direct mutations prove comparisons, exports, downstream/subprocess scrubs,
# mask scans, recursion, required gitlinks, raw identity, fixed config, and
# ignored-file detection are load-bearing. Live release uses a pristine,
# untransformed checkout with its exact artifact external; this ordinary build
# may retain ignored bin/obj because only the scratch proof invokes the checker.
echo "=== release-provenance gate proof (FG-093, blocking) ==="
./scripts/prove-release-provenance.sh \
  || { echo "RELEASE-PROVENANCE PROOF FAILED"; exit 1; }

# FG-166. The live freshness warning is corpus-host-only, but its case-to-receipt
# mapping is pure filename/mtime logic and must be proven everywhere. The scratch
# proof holds literal `.b1` singleton names apart from multi-build `.b1` receipts,
# checks every emitted build independently, and refuses name collisions before a
# map can silently deduplicate them.
echo "=== scorecard receipt-mapping proof (FG-166, blocking) ==="
./scripts/prove-scorecard-receipt-mapping.sh \
  || { echo "SCORECARD RECEIPT-MAPPING PROOF FAILED"; exit 1; }

# FG-094. The live comparison needs the private 228-file corpus, a pinned
# Jenkins oracle and an operator-provided external baseline, so it cannot run
# in ordinary corpus-free CI. The self-contained proof still runs everywhere.
# It plants filename-set regressions (including equal-count swaps), schema and
# digest damage, reached_agent/compiled confusion, Git object replacement, and
# mutations that make each comparison incorrectly executable.
echo "=== compatibility-regression gate proof (FG-094, blocking) ==="
./scripts/prove-compatibility-regression.sh \
  || { echo "COMPATIBILITY-REGRESSION PROOF FAILED"; exit 1; }

# FG-161. Every committed receipt's seal, RECOMPUTED from the receipt's own content.
# The scorecard classifies a receipt as proven by reading its VERDICT LINE, and nothing
# re-derived the hash that claim rests on — a receipt edited with that line left intact
# inflated the published count and `--check` approved it.
#
# Runs EVERYWHERE including CI, unlike the corpus-dependent scorecard check below:
# verification needs no Jenkins, no corpus and no case files, because the case digest is
# recorded in the receipt and bound by the seal.
dotnet run --project tools/Fogell.Differential.Cli/Fogell.Differential.Cli.fsproj -c Release --no-build \
  -- --verify-seals differential/receipts || { echo "SEAL VERIFICATION FAILED"; exit 1; }
./scripts/prove-seal-verification.sh || { echo "SEAL VERIFICATION PROOF FAILED"; exit 1; }

# FG-090/091/092/094. The published compatibility artifacts are GENERATED, and
# the regression checker runs their --check before comparing exact filename
# sets — ONLY ON A HOST THAT HAS THE CORPUS.
#
# CI does not: the corpus lives outside the repo (see the workflow header), so on
# GitHub this check does not run and stale artifacts WOULD pass. That is a real
# hole, stated here rather than papered over: drift is caught on luigi/HeMan and
# nowhere else. Hard-failing instead would break every CI run, which trades a
# gap in coverage for a gate nobody can pass.
if [ -d "${FOGELL_CORPUS:-/sn8100/work/exchange/crucible-gate/corpus}" ]; then
  ./scripts/check-compatibility-regression.py \
    || { echo "COMPATIBILITY REGRESSION OR SCORECARD CHECK FAILED"; exit 1; }
else
  echo "scorecard/regression check NOT RUN: corpus not mounted — generated artifacts and live compatibility non-regression are UNVERIFIED on this host"
fi
./scripts/bin/audit-stale-refs "${FOGELL_STALE_REF_BASE:-origin/main}" --strict \
  || { echo "STALE REFERENCE AUDIT FAILED"; exit 1; }

# FG-112: the restart lane is self-contained (dotnet + bash + a SIGKILL) and
# is the ONLY automated coverage of PersistenceHooks/resume — it runs in the
# gate so the headline durability semantics cannot silently regress.
echo "=== restart lane (FG-112, blocking) ==="
./scripts/run-restart-lane.sh || { echo "RESTART LANE FAILED"; exit 1; }

# FG-046b: same argument for durable APPROVAL — a human's answer surviving a
# kill is the one guarantee no receipt can cover (the differential harness has
# no approver on either side), so its only proof is this lane.
# FG-046e: scenario N's watcher earns its place only if it is proven to REPORT
# a breach. Runs first, on planted overlaps, in scratch directories.
echo "=== inbox-watcher proof (FG-046e, blocking) ==="
./scripts/prove-approval-watcher.sh || { echo "APPROVAL-WATCH PROOF FAILED"; exit 1; }

echo "=== approval lane (FG-046b, blocking) ==="
./scripts/run-approval-lane.sh || { echo "APPROVAL LANE FAILED"; exit 1; }

echo "OK"
