#!/usr/bin/env bash
# FG-000/FG-001 — the gate every ticket must pass before its PR.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
echo "=== sdk ==="; dotnet --version
./scripts/prove-dependency-locks.sh \
  || { echo "DEPENDENCY-LOCK/SOURCE-CLEARED BUILD PROOF FAILED"; exit 1; }
echo "=== tests ==="
fail=0
for p in tests/*/; do
  [ -f "$p/$(basename "$p").fsproj" ] || continue
  echo "--- $(basename "$p") ---"
  # Capture, then decide what to show. Piping straight into `rg -o "EXPECTO! .*"`
  # printed the summary and DISCARDED every failure detail, so a red gate said
  # "1 failed" and named neither the test nor the assertion. That is what CI did
  # on PR #36 while the same suite passed locally: the one run that could have
  # explained an environment-dependent failure threw the explanation away.
  # A gate that cannot be diagnosed from its own output is a gate you re-run
  # instead of read.
  test_out="$(dotnet run --project "$p" -c Release --no-build 2>&1)"
  test_rc=$?
  if [ "$test_rc" -ne 0 ]; then
    fail=1
    printf '%s\n' "$test_out"
  else
    printf '%s\n' "$test_out" | rg -o "EXPECTO! .*" | tail -1
  fi
done
[ "$fail" -ne 0 ] && { echo "TESTS FAILED"; exit 1; }
# FG-104. BLOCKING. Every MEASURED claim must cite a receipt or admit UNPROVEN. The
# backlog it was introduced against (30) is zero, so the check now fails the build instead
# of printing at it — an advisory check nobody must act on decays into noise.
if [ -x scripts/audit-claims.bb ]; then
  echo "=== claim audit + its citation proof (FG-104/FG-174, blocking) ==="
  # THE PROOF RUNS FIRST, for the same reason the stale-ref proof does below. The audit
  # gained a SECOND check — every receipt a comment NAMES must exist — and a checker
  # nobody has watched fail is itself a claim. 14 arms in both directions: six planted
  # dangling citations it must reject, and the real spellings (backticked, colon,
  # multi-build `.b1`, glob, `.receipt.txt`, and prose that merely says "receipt") it
  # must not. Both directions earned their place — the first draft called six genuine
  # citations dangling, and its ACCEPT arms were passing on zero scanned files.
  ./scripts/prove-claim-citations.sh || { echo "CLAIM-CITATION PROOF FAILED"; exit 1; }
  # No `| head`: piping into head can SIGPIPE babashka and mask its exit status, which
  # would make an advisory check silently become a broken one.
  # Status captured explicitly: without it a babashka that cannot start is indistinguishable
  # from a clean audit, and the planned `--strict` flip would never have failed a build.
  if ! audit_out="$(./scripts/audit-claims.bb --strict 2>&1)"; then
    echo "CLAIM AUDIT FAILED"; printf '%s\n' "$audit_out"; exit 1
  fi
  printf '%s\n' "$audit_out" | sed -n '1,3p'

fi

# FG-104b: a comment naming a mechanism the code no longer has. Three of those
# landed in one day and every one was caught by a reviewer rather than a check —
# `audit-claims.bb` asks a different question (does a MEASURED claim name a
# receipt) that a stale identifier passes trivially.
#
# Scope, stated exactly because vaguer versions of this sentence have been a
# finding TWICE: F# BINDINGS of four or more characters — let/member/type/
# override/default/and declarations and PascalCase record fields — named in comments
# or nested `(* ... *)` blocks. Short names are deliberately out (`x`, `id`,
# `ctx` occur in ordinary English and a checker that fires on prose gets
# switched off), and non-F# symbols are not extracted at all: a deleted shell
# function or bb def can leave a stale comment this audit will not see.
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
# through the real parser/interpreter/host path, prove sanctioned calls still
# work, and prove the checker rejects timeout/signal execution states plus six
# record/workspace mutations: unique non-failure terminal, extra terminal,
# generic-failure, unnamed, missing-boundary-reason, and no-halt. The proof binds
# the exact current-worktree net10 Release host; no ambient binary override exists.
echo "=== sandbox-denial proof (FG-072, blocking) ==="
./scripts/prove-sandbox-denials.sh || { echo "SANDBOX-DENIAL PROOF FAILED"; exit 1; }

# FG-162. Board rows quoting generated counts are re-derived from the committed
# ledger. Runs EVERYWHERE including CI — both files are in the repo, unlike the
# corpus-dependent scorecard check below.
./scripts/audit-board-numbers.bb || { echo "BOARD-NUMBER AUDIT FAILED"; exit 1; }
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

# FG-090/091/092. The published compatibility artifacts are GENERATED, and this
# checks they match the evidence — ONLY ON A HOST THAT HAS THE CORPUS.
#
# CI does not: the corpus lives outside the repo (see the workflow header), so on
# GitHub this check does not run and stale artifacts WOULD pass. That is a real
# hole, stated here rather than papered over: drift is caught on luigi/HeMan and
# nowhere else. Hard-failing instead would break every CI run, which trades a
# gap in coverage for a gate nobody can pass.
if [ -d "${FOGELL_CORPUS:-/sn8100/work/exchange/crucible-gate/corpus}" ]; then
  ./scripts/generate-scorecard.bb --check || { echo "SCORECARD STALE"; exit 1; }
else
  echo "scorecard check NOT RUN: corpus not mounted — generated artifacts are UNVERIFIED on this host"
fi
./scripts/audit-stale-refs.bb "${FOGELL_STALE_REF_BASE:-origin/main}" --strict \
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
