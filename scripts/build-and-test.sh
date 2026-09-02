#!/usr/bin/env bash
# FG-000/FG-001 — the gate every ticket must pass before its PR.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 1

# LANES. Locally this script is one sequence and `FOGELL_GATE_LANES` is unset:
# everything below runs, in this order, and the final line is a bare `OK`. The
# hosted workflow runs the SAME script once per lane in parallel jobs, because a
# single 4-core runner took 20 minutes wall clock for work whose parts do not
# depend on each other (run 33567174871): the fflat compiles alone were ~7
# minutes, sequential there, while the dotnet build and the lanes sat waiting.
#
# The lanes are declared HERE, not in the workflow, and the workflow derives its
# matrix from `--list-lanes`, so the two cannot drift apart. Every blocking block
# below is wrapped in `if lane_active <lane>`, and the compiled audit tool
# `scripts/bin/audit-gate-lanes` (scripts/fsx/audit-gate-lanes.fsx) checks —
# statically, on the script text — that every invocation it recognises sits
# inside a listed lane block, that no lane block has an else arm, and that every
# listed lane owns a block. It runs in the `audits` lane once the tools exist,
# so a partition hole fails that lane and with it the aggregate check. A subset
# run says so on its last line: only the unset/`all` form is the gate.
#
# WHAT A LANE MAY DEPEND ON is only what its own blocks produce plus the checkout
# and the SDK: the hosted lane jobs share nothing. In particular the fflat-built
# tools in scripts/bin exist only where `ensure_audit_tools` ran, so a proof
# that reads one belongs in `audits` even when it looks like a build-tree proof —
# the scorecard receipt-mapping proof (FG-166) is there for exactly that reason,
# which moves it a few blocks earlier in the `all` order than it used to sit.
#
#   build       the solution build (inside the dependency-lock proof), the test
#               projects, and every proof that needs the built tree
#   audits      the fflat-compiled audit tools, their proofs, the claim and
#               board-number audits, the lane-partition audit — no dotnet build
#   prelude     the shared-prelude semantics proof, which compiles its own
#               fixtures with fflat and needs none of the nine tools; 122 s of
#               fflat work (run 33595804856) that stood on the audits lane's path
#   stale-refs  the stale-reference audit and its mutant proof — the longest
#               fflat consumer, so it stands alone; it builds only the one tool
#               it runs
#   lanes       the restart lane, the inbox-watcher proof and the approval lane;
#               the restart lane's own `dotnet build` produces the tree all three
#               use
GATE_LANES=(build audits prelude stale-refs lanes)

if [ "${1:-}" = "--list-lanes" ]; then
  printf '%s\n' "${GATE_LANES[@]}"
  exit 0
fi

# `-` not `:-`: unset means the whole gate, but a variable that is SET and empty
# is a caller that meant to name a lane and did not, and is refused below.
gate_lanes_requested="${FOGELL_GATE_LANES-all}"
lane_known() {
  local lane
  for lane in "${GATE_LANES[@]}"; do [ "$lane" = "$1" ] && return 0; done
  return 1
}
if [ "$gate_lanes_requested" != all ]; then
  [[ -n "${gate_lanes_requested//[[:space:]]/}" ]] \
    || { echo "FOGELL_GATE_LANES is set but names no lane (known: ${GATE_LANES[*]})"; exit 2; }
  for lane in $gate_lanes_requested; do
    lane_known "$lane" \
      || { echo "unknown gate lane '$lane' (known: ${GATE_LANES[*]})"; exit 2; }
  done
fi
# lane_active <lane> — true when this run should execute that lane's blocks. A
# block naming a lane the script does not list is a partition defect, and it
# exits rather than silently running nowhere.
lane_active() {
  lane_known "$1" || { echo "INTERNAL: a gate block names unknown lane '$1'"; exit 2; }
  [ "$gate_lanes_requested" = all ] && return 0
  local lane
  for lane in $gate_lanes_requested; do [ "$lane" = "$1" ] && return 0; done
  return 1
}

# ONE NUGET PACKAGE CACHE FOR THE WHOLE GATE. prove-dependency-locks.sh fills it
# from empty — that emptiness is part of its proof — and every later dotnet
# invocation reads the same directory, so the assets files its no-restore build
# wrote stay valid and the plain `dotnet build` calls further down (FG-207, the
# restart lane, the approval lane) are incremental no-ops rather than a second
# full compile of the solution behind a cache that had been deleted under them.
# Measured locally on 2026-09-01: the next solution build after the proof ran 0
# compiler invocations with the cache kept, 26 with it deleted. Hosted, the
# FG-207 proof went 30 s → 6 s and the restart lane's build 32 s → 4 s (run
# 33571850280 against 33567174871). In a lane that skips the build, the same
# directory is where the lane's own `dotnet build` restores to, so one lane
# never reads another's state and none of them touches ~/.nuget/packages.
# Run-scoped and removed at exit: a stale cache is never reused across runs.
# The cost moves to AFTER the gate: the assets files then name a deleted cache,
# so a developer's next plain `dotnet build` restores into ~/.nuget/packages
# and recompiles once. That is the rebuild that used to happen inside the gate.
# Resolved with `pwd -P` to match the proof's own resolution of the same path;
# a symlinked /tmp would otherwise hash two spellings of one cache as different.
# Fail closed: this script runs without `-e`, and an empty NUGET_PACKAGES from a
# failed mktemp or cd would silently fall back to ~/.nuget/packages — the exact
# behaviour this block exists to remove, with nothing in the log to say so.
gate_package_cache_created="$(mktemp -d /tmp/fogell-gate-nuget.XXXXXX)" \
  || { echo "GATE PACKAGE CACHE: mktemp failed"; exit 1; }
gate_package_cache="$(cd -- "$gate_package_cache_created" && pwd -P)" \
  || { echo "GATE PACKAGE CACHE: could not resolve $gate_package_cache_created"
       rm -rf -- "$gate_package_cache_created"; exit 1; }
[ -n "$gate_package_cache" ] \
  || { echo "GATE PACKAGE CACHE: resolved to an empty path"
       rm -rf -- "$gate_package_cache_created"; exit 1; }
trap 'rm -rf -- "$gate_package_cache"' EXIT
export FOGELL_LOCK_PROOF_PACKAGE_CACHE="$gate_package_cache"
export NUGET_PACKAGES="$gate_package_cache"

# FG-226. The audit tools are compiled from scripts/fsx/*.fsx, never committed:
# an fflat build is not reproducible, so a committed binary could not be proven
# to match its source. Building them in the run that trusts them makes the
# source-to-binary link true by construction. Two lanes need them (`audits` and
# `stale-refs`); each tool is built at most once per run, by whichever block
# asks first. With no arguments every tool is built — the audits block, and so
# the local `all` sequence compiles them exactly where it always did. With tool
# names only those not yet built are compiled, which is how a lane running one
# audit pays for one compile instead of nine.
audit_tools_built=""   # space-separated tool names, or `all`
ensure_audit_tools() {
  [ "$audit_tools_built" = all ] && return 0
  if [ "$#" -eq 0 ]; then
    ./scripts/build-audits.sh || { echo "AUDIT TOOL BUILD FAILED"; exit 1; }
    audit_tools_built=all
    return 0
  fi
  local missing=() t
  for t in "$@"; do
    case " $audit_tools_built " in *" $t "*) ;; *) missing+=("$t") ;; esac
  done
  [ "${#missing[@]}" -eq 0 ] && return 0
  ./scripts/build-audits.sh "${missing[@]}" || { echo "AUDIT TOOL BUILD FAILED"; exit 1; }
  audit_tools_built="$audit_tools_built ${missing[*]}"
}

echo "=== sdk ==="; dotnet --version
if lane_active build; then
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
fi

if lane_active audits; then
  # FG-104. BLOCKING. Every MEASURED claim must cite a receipt or admit UNPROVEN. The
  # backlog it was introduced against (30) is zero, so the check now fails the build instead
  # of printing at it — an advisory check nobody must act on decays into noise.
  ensure_audit_tools
  # FG-226. Review found three error paths a success-only port comparison could
  # not see: a stale fflat version broke discovery, malformed crumb JSON silently
  # removed authentication, and failed setup POSTs were ignored. Prove those
  # known-bad environments before trusting the native tools.
  ./scripts/prove-fg226-audit-tools.sh || { echo "FG-226 AUDIT TOOL PROOF FAILED"; exit 1; }
fi

if lane_active prelude; then
  # The prelude is shared by all nine tools, so a divergence in it changes every
  # audit's verdict at once. In the `all` sequence it is proven right after the
  # tools are built and the FG-226 toolchain proof has run (which already
  # exercises `probe-input`), before the audits themselves. Its own lane hosted,
  # because it compiles its fixtures with fflat and needs none of the tools, so
  # it can run beside the tool build instead of after it.
  ./scripts/prove-fsx-prelude.sh || { echo "FSX PRELUDE PROOF FAILED"; exit 1; }
fi

if lane_active audits; then
  # The lane partition of THIS script (see the LANES comment at the top). Static,
  # sub-second, and it proves its own checker on planted defects first.
  echo "=== gate lane partition audit (blocking) ==="
  ./scripts/bin/audit-gate-lanes || { echo "GATE LANE PARTITION AUDIT FAILED"; exit 1; }

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
fi

if lane_active stale-refs; then
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
  # Only the one tool this lane runs; in the `all` sequence the audits block has
  # already built everything and this is a no-op.
  ensure_audit_tools audit-stale-refs
  # the proof runs FIRST and in scratch repositories: a checker nobody has watched
  # fail is a claim, and this one has twice been wrong about its own job
  ./scripts/prove-stale-refs.sh || { echo "STALE-REF PROOF FAILED"; exit 1; }
fi

if lane_active build; then
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
fi

if lane_active audits; then
  # FG-162. Board rows quoting generated counts are re-derived from the committed
  # ledger. Runs EVERYWHERE including CI — both files are in the repo, unlike the
  # corpus-dependent scorecard check below.
  ./scripts/bin/audit-board-numbers || { echo "BOARD-NUMBER AUDIT FAILED"; exit 1; }
  ./scripts/prove-board-numbers.sh || { echo "BOARD-NUMBER PROOF FAILED"; exit 1; }

  # FG-166. The live freshness warning is corpus-host-only, but its case-to-receipt
  # mapping is pure filename/mtime logic and must be proven everywhere. The scratch
  # proof holds literal `.b1` singleton names apart from multi-build `.b1` receipts,
  # checks every emitted build independently, and refuses name collisions before a
  # map can silently deduplicate them. In THIS lane because it runs the compiled
  # `generate-scorecard` (and `build-audits.sh --check`) against a stubbed dotnet:
  # it needs the audit tools, not the built tree.
  echo "=== scorecard receipt-mapping proof (FG-166, blocking) ==="
  ./scripts/prove-scorecard-receipt-mapping.sh \
    || { echo "SCORECARD RECEIPT-MAPPING PROOF FAILED"; exit 1; }
fi

if lane_active build; then
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
fi

if lane_active stale-refs; then
  ./scripts/bin/audit-stale-refs "${FOGELL_STALE_REF_BASE:-origin/main}" --strict \
    || { echo "STALE REFERENCE AUDIT FAILED"; exit 1; }
fi

if lane_active lanes; then
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
fi

if [ "$gate_lanes_requested" = all ]; then
  echo "OK"
else
  # Deliberately NOT the bare `OK`: a lane subset is one share of the gate, and
  # nothing downstream may mistake it for the whole.
  echo "OK (lanes: $gate_lanes_requested — a subset, not the full gate)"
fi
