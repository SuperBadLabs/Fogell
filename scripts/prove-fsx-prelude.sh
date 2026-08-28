#!/usr/bin/env bash
# FG-226. Proves `scripts/fsx/prelude.fsx` reproduces the java.util.regex and
# java.lang.String semantics the eight ported audits were written against.
#
# WHY THIS EXISTS AND WHY IT IS NOT A COMPARISON AGAINST BABASHKA. The port
# replaced babashka, so a proof that shells out to `bb` would pin the new tools
# to a dependency the port exists to remove, and would simply not run on a host
# without it. The expected values below were DERIVED by running the twin
# `bb` program on 2026-08-27 and are pinned here as constants; this script is
# what keeps them true.
#
# THE TWO DIVERGENCES THAT MOTIVATE IT, both measured rather than anticipated:
#
#   - `Char.IsWhiteSpace` counts U+00A0 as whitespace and Java's
#     `Character.isWhitespace` does not, and .NET additionally counts U+0085.
#     THE EXPOSURE IS LATENT: the tracked tree holds none of these code points,
#     so no ported audit changes its verdict on today's inputs. This header said
#     the execution board was "full of NBSP"; it contains zero. The fix is right
#     and the evidence originally cited for it was invented — corrected in four
#     other places before a fresh verifier found the copy here.
#   - .NET's `\s` and `\d` are Unicode-aware and Java's are not, so a pattern
#     copied across unchanged silently changes which lines it matches.
#
# Neither is visible in a passing gate — both would quietly change a VERDICT —
# so each is asserted here and each is proven to fail under a planted mutation.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
PRELUDE="$PWD/scripts/fsx/prelude.fsx"
LAB=$(mktemp -d /tmp/fogell-prelude-proof.XXXXXX)
trap 'rm -rf "$LAB"' EXIT
FAILED=0

command -v fflat >/dev/null 2>&1 || {
  echo "FAIL: fflat not on PATH — install with: dotnet tool install -g fflat" >&2
  exit 1
}

# The fixture prints one `name|value` per semantic under test. Written once and
# compiled against whichever prelude the arm supplies.
cat > "$LAB/fixture.fsx" <<'FIXTURE'
#load "prelude.fsx"
open System
open System.Text.RegularExpressions
open Prelude
let b (v: bool) = if v then "true" else "false"
let show (n: string) (v: string) = out (n + "|" + v)
let nbsp = string (char 0x00A0)
let em = string (char 0x2003)
let ideo = string (char 0x3000)
let nel = string (char 0x0085)
let vtab = string (char 0x000B)
// Reported as CODE POINTS so the golden file below stays pure ASCII: an
// expected file containing a literal NBSP is one careless editor away from
// becoming a space, and the arm would then pass for the wrong reason.
let points (v: string) = String.Join(",", v |> Seq.map (fun c -> string (int c)))
show "trim-nbsp" (points (javaTrim (nbsp + "x" + nbsp)))
show "trim-space" (points (javaTrim "  x  "))
// `clojure.string/trim` trims by Character/isWhitespace, NOT String.trim. The
// two pinned rows above pass under EITHER semantics, so they proved nothing
// about which one this is; these separate them.
show "trim-em" (points (javaTrim (em + "x" + em)))
show "trim-ideo" (points (javaTrim (ideo + "x" + ideo)))
show "blank-nel" (b (blank nel))
show "s-vtab" (b ((javaRx "\\s").IsMatch vtab))
show "blank-nbsp" (b (blank nbsp))
show "blank-spaces" (b (blank "   "))
show "blank-x" (b (blank " x "))
show "s-nbsp" (b ((javaRx "\\s").IsMatch nbsp))
show "s-space" (b ((javaRx "\\s").IsMatch " "))
show "d-arabic" (b ((javaRx "\\d").IsMatch "١"))
show "d-ascii" (b ((javaRx "\\d").IsMatch "7"))
show "class-nbsp" (b ((javaRx "[\\s;]").IsMatch nbsp))
show "class-semi" (b ((javaRx "[\\s;]").IsMatch ";"))
show "splitlines" (String.Join(",", splitLines "a\nb\n\n"))
// A RELATIVE PROGRAM PATH MUST RESOLVE AGAINST `dir`, as `babashka.process/shell`
// does with `:dir`, NOT against the caller's cwd as .NET's ProcessStartInfo
// does. Unpinned, that divergence made a blocking proof's stubbed
// `scripts/verify-corpus.sh` be bypassed in favour of the real repository's
// copy — green on a machine with a corpus mounted, red on a hosted runner.
// Two scratch dirs, each holding a DIFFERENT script of the same relative name;
// the row reports which one actually ran.
// The two trees are built by the PROOF, under its own $LAB so its trap removes
// them, and handed over in the environment. Building them here instead leaked a
// directory per fixture execution, and keyed them on a
// `GetCurrentDirectory().GetHashCode()` that .NET randomises per process, so the
// "stable per checkout" name was neither stable nor cleaned up.
let progDir = Environment.GetEnvironmentVariable "FOGELL_PROG_DIR"
let progCwd = Environment.GetEnvironmentVariable "FOGELL_PROG_CWD"
// BOTH CANDIDATE PROGRAMS EXIST, so the row reports WHICH ONE RAN rather than
// merely whether one did. `sub/probe.sh` is present under the `dir` passed to
// `runIn` AND under the process's cwd, printing different words. A correct
// `runIn` prints RESOLVED-AGAINST-DIR; one that keeps .NET's own behaviour finds
// the cwd copy and prints RESOLVED-AGAINST-CWD. An earlier version of this arm
// planted only one script, so the mutant crashed instead of answering, and the
// proof's own no-row guard then refused the kill — correctly, since a mutant
// that dies before printing proves nothing. The guard is still honoured below.
let launched (dir: string) (exe: string) (args: string list) =
    try javaTrim (runIn dir [] exe args).Out with _ -> "LAUNCH-FAILED"
let prior = IO.Directory.GetCurrentDirectory()
IO.Directory.SetCurrentDirectory progCwd
show "prog-relative-dir" (launched progDir "sub/probe.sh" [])
// A BARE NAME MUST STILL GO TO PATH, which is also what babashka does, and it is
// load-bearing: `prove-scorecard-receipt-mapping.sh` stubs `dotnet` by prepending
// a directory to PATH, and resolving bare names against `dir` would break it.
show "prog-bare-name-uses-path" (launched progDir "echo" [ "PATH-LOOKUP" ])
IO.Directory.SetCurrentDirectory prior
// EXHAUSTIVE MODE. The pinned rows above cover roughly eight code points, which
// is enough to separate the candidate semantics that have actually been wrong
// but NOT enough to back the claim that Java and .NET agree everywhere. This
// emits one row per UTF-16 code unit so the whole differential can be hashed.
let table () =
    let sb = Text.StringBuilder()
    let f (re: Regex) (str: string) = if re.IsMatch str then "1" else "0"
    let rs = javaRx "\\s"
    let rS = javaRx "\\S"
    let rd = javaRx "\\d"
    let rD = javaRx "\\D"
    let rw = javaRx "\\w"
    let rW = javaRx "\\W"
    // THE IN-CLASS BRANCH TOO. `javaPattern` rewrites a shorthand differently
    // inside `[...]`, where it contributes MEMBERS rather than a nested class,
    // and every row above takes only the non-class path. The verifier removed
    // vertical tab from the in-class arm alone and this table, plus all sixteen
    // pinned rows, stayed green while `[\s;]` stopped matching VT. A checker
    // blind at its own edge is the defect this whole file exists to catch.
    let cs = javaRx "[\\s]"
    let cd = javaRx "[\\d]"
    let cw = javaRx "[\\w]"
    // AND THE CLASS-TRACKING STATE MACHINE. Every pattern above is a single
    // construct, so `javaPattern`'s `]`-close reset is never exercised: a
    // mutant that never leaves in-class state passes all sixteen fixtures AND
    // reproduces the digest, while rewriting `(\d+)` to `(0-9+)` and silently
    // breaking the board-accounting tokens. This pattern has a class, a close,
    // and a shorthand AFTER it, so the reset is load-bearing for its result.
    // TWO classes, not one. A single class exercises only the FIRST close, so a
    // tracker that closes once and then never again reproduces the digest,
    // passes every fixture, and still changes `audit-stale-refs`' verdict on a
    // real base. Live patterns — `definitionParenRx`, `citeColon` — carry two
    // classes each, so this is a shape the tools actually use.
    let cx = javaRx "[;]?[,]?\\d"
    for i in 0 .. 65535 do
        let c = char i
        let str = string c
        sb.Append(string i).Append("|")
          .Append(if javaIsWhitespace c then "1" else "0")
          .Append(f rs str).Append(f rS str).Append(f rd str)
          .Append(f rD str).Append(f rw str).Append(f rW str)
          .Append(f cs str).Append(f cd str).Append(f cw str).Append(f cx str)
          .Append("\n") |> ignore
    Console.Out.Write(sb.ToString())

[<EntryPoint>]
let main argv =
    if argv |> Array.contains "--table" then table ()
    0
FIXTURE

# Derived from the babashka twin on 2026-08-27. Any change here is a change to
# what the ported audits mean.
cat > "$LAB/expected" <<'EXPECTED'
trim-nbsp|160,120,160
trim-space|120
trim-em|120
trim-ideo|120
blank-nel|false
s-vtab|true
blank-nbsp|false
blank-spaces|true
blank-x|false
s-nbsp|false
s-space|true
d-arabic|false
d-ascii|true
class-nbsp|false
class-semi|true
splitlines|a,b
prog-relative-dir|RESOLVED-AGAINST-DIR
prog-bare-name-uses-path|PATH-LOOKUP
EXPECTED

# ARMS ARE PREPARED, THEN COMPILED IN PARALLEL, THEN JUDGED. Four sequential
# compiles cost ~23s of wall time for work that is embarrassingly parallel: each
# arm is a separate prelude in its own directory and shares nothing. The build
# is a distinct phase from the judging so no arm can read a half-built binary.
ARM_DIRS=()
arm_prepare() {
  local dir=$1
  mkdir -p "$dir"
  cp "$LAB/fixture.fsx" "$dir/fixture.fsx"
  ARM_DIRS+=("$dir")
}
arm_build_all() {
  local jobs_max d
  jobs_max=$(nproc 2>/dev/null || echo 4)
  jobs_max=$(( jobs_max / 3 )); [ "$jobs_max" -lt 1 ] && jobs_max=1
  for d in "${ARM_DIRS[@]}"; do
    while [ "$(jobs -rp | wc -l)" -ge "$jobs_max" ]; do wait -n; done
    { fflat "$d/fixture.fsx" -o "$d/fixture" >"$d/compile.log" 2>&1 || : ; } &
  done
  wait
}
# arm_output <dir> — the arm's stdout, or a marker when it never built
arm_output() {
  local dir=$1
  # `|| true`: a mutant is EXPECTED to be able to crash, and under `set -e` a
  # non-zero exit here aborts the whole proof before the judging block can say
  # which arm failed and why. Failing closed with no diagnostic is still a
  # failure, but it is the kind nobody can act on.
  if [ -x "$dir/fixture" ]; then "$dir/fixture" 2>&1 || true; else echo "COMPILE-FAILED"; fi
}

# The two competing program trees for the relative-path arm. Built here, under
# $LAB, so the EXIT trap removes them.
mkdir -p "$LAB/prog/sub" "$LAB/prog-cwd/sub"
printf '#!/bin/sh\necho RESOLVED-AGAINST-DIR\n' > "$LAB/prog/sub/probe.sh"
printf '#!/bin/sh\necho RESOLVED-AGAINST-CWD\n' > "$LAB/prog-cwd/sub/probe.sh"
chmod +x "$LAB/prog/sub/probe.sh" "$LAB/prog-cwd/sub/probe.sh"
export FOGELL_PROG_DIR="$LAB/prog" FOGELL_PROG_CWD="$LAB/prog-cwd"

echo "=== fsx prelude: java semantics pinned ==="

# ---- prepare every arm, then build them together
d="$LAB/base"; arm_prepare "$d"; cp "$PRELUDE" "$d/prelude.fsx"

# `label` `sed-expression` — staged here, judged after the build
MUT_LABELS=(); MUT_ROWS=()
stage_mutation() {
  local label=$1 expr=$2 row=$3
  local md="$LAB/mut-$(echo "$label" | tr -c 'a-zA-Z0-9' '-')"
  arm_prepare "$md"
  sed "$expr" "$PRELUDE" > "$md/prelude.fsx"
  # ONE LINE, like `replace_line_once` in prove-stale-refs.sh. Raw sed will
  # happily rewrite several lines, and a mutation whose blast radius is unknown
  # cannot attribute its kill to the rule under test.
  local changed
  changed=$(diff "$PRELUDE" "$md/prelude.fsx" | grep -c "^<" || true)
  if [ "$changed" -ne 1 ]; then
    echo "  FAIL: $label — mutation changed $changed lines, expected exactly 1"
    FAILED=1
  fi
  MUT_LABELS+=("$label"); MUT_ROWS+=("$row")
}

# Java excludes the non-breaking spaces from isWhitespace; .NET includes them.
stage_mutation "nbsp whitespace" \
  's|^    if c = .\\u00A0. .*$|    if false then false|' \
  blank-nbsp

# The whole point of javaPattern: without the shorthand rewrite, .NET's Unicode
# \s matches NBSP and \d matches Arabic-Indic digits.
stage_mutation "shorthand class rewrite" \
  's|^let javaPattern (p: string) =$|let javaPattern (p: string) = p\nlet unusedJavaPattern (p: string) =|' \
  s-nbsp

# The exact defect the verifier caught: trimming by String.trim semantics
# (code points <= U+0020) instead of Character/isWhitespace.
stage_mutation "trim by String.trim" \
  's|^    while i < j \&\& javaIsWhitespace s\.\[i\] do i <- i + 1$|    while i < j \&\& s.[i] <= (char 32) do i <- i + 1|' \
  trim-em

# U+0085 NEL is whitespace to .NET and not to Java.
stage_mutation "nel exclusion" \
  's|^    if c = .\\u00A0. .. c = .\\u2007. .. c = .\\u202F. .. c = .\\u0085. then false$|    if c = (char 0x00A0) then false|' \
  blank-nel

# Java's \s includes VERTICAL TAB; dropping it silently narrows every pattern.
stage_mutation "vertical tab in \\s" \
  '/Some "\[ /s|\\011||' \
  s-vtab

# Trailing empties: Clojure drops them, Regex.Split keeps them.
stage_mutation "trailing empty drop" \
  's|^    go (List.rev xs)$|    xs|' \
  splitlines

# THE DEFECT CI CAUGHT. Reverting `runIn` to .NET's own resolution makes a
# relative program path resolve against the caller's cwd, so the arm finds no
# `probe.sh` there and fails rather than running the one beside `dir`.
stage_mutation "relative program path against cwd" \
  's|^        then Path.GetFullPath(Path.Combine(dir, exe))$|        then exe|' \
  prog-relative-dir

arm_build_all

# ---- THE EXHAUSTIVE DIFFERENTIAL, pinned as a digest.
#
# `Character.isWhitespace` plus `\s \S \d \D \w \W` over ALL 65 536 UTF-16
# code units, derived from babashka 1.12.217 on 2026-08-28. Sixteen sample rows
# cannot back a claim that Java and .NET agree everywhere; this can. Pinning the
# DIGEST rather than the table keeps 65 536 lines out of the repository while
# still failing closed on any divergence — including in a code point no fixture
# thought to name.
JAVA_TABLE_SHA256=86ad074649fe21d572a85120b97a9c14788e03cfd64b27eefb8e24aa137db026

# ---- the real prelude must match the pinned values exactly
arm_output "$d" > "$LAB/base.out"
if diff -u "$LAB/expected" "$LAB/base.out" > "$LAB/base.diff"; then
  echo "  passed  prelude reproduces every pinned java semantic"
  # Filtered to TABLE ROWS ONLY. The fixture's pinned rows are top-level side
  # effects that print under any argument, and folding them into the digest
  # would make it change whenever a pinned row is added — coupling two
  # independent checks and inviting someone to "fix" the java digest by hand.
  # `|| true` and an explicit ROW COUNT. Under `set -euo pipefail` a table that
  # emits no matching rows propagates grep's exit 1 through the substitution and
  # kills the script at this line with NO message — failing closed, but silently,
  # the same shape fixed in `arm_output` last round. It fired for real when the
  # table gained three columns and this filter still said seven.
  "$d/fixture" --table 2>/dev/null | grep -E '^[0-9]+\|[01]{11}$' > "$d/table.out" || true
  rows=$(wc -l < "$d/table.out")
  actual=$(sha256sum < "$d/table.out" | cut -d' ' -f1)
  if [ "$rows" -ne 65536 ]; then
    echo "  FAIL: exhaustive table emitted $rows rows, expected 65536"
    FAILED=1
  elif [ "$actual" = "$JAVA_TABLE_SHA256" ]; then
    echo "  passed  exhaustive differential: 65536/65536 code units match java"
  else
    echo "  FAIL: the exhaustive character-class differential diverges from java"
    echo "        expected $JAVA_TABLE_SHA256"
    echo "        actual   $actual"
    FAILED=1
  fi
else
  echo "  FAIL: the prelude no longer matches the pinned java semantics"
  sed 's/^/    | /' "$LAB/base.diff"
  FAILED=1
fi

# ---- each mutation must be KILLED: a planted defect the pinned values catch
i=0
while [ "$i" -lt "${#MUT_LABELS[@]}" ]; do
  label="${MUT_LABELS[$i]}"; row="${MUT_ROWS[$i]}"
  md="$LAB/mut-$(echo "$label" | tr -c 'a-zA-Z0-9' '-')"
  if cmp -s "$PRELUDE" "$md/prelude.fsx"; then
    echo "  FAIL: $label — mutation changed NOTHING, so it proves nothing"
    FAILED=1
  else
    arm_output "$md" > "$md/out" 2>&1
    before=$(grep "^$row|" "$LAB/expected" || true)
    after=$(grep "^$row|" "$md/out" || true)
    if grep -q '^COMPILE-FAILED$' "$md/out"; then
      # A MUTANT THAT DOES NOT COMPILE HAS NOT BEEN TESTED. The row is simply
      # absent from its output, which compares unequal to the pinned value and
      # would otherwise report a kill this arm never earned. `prove-stale-refs.sh`
      # has carried this guard since it was written; this file did not, and the
      # vertical-tab arm passed vacuously until the pre-push verifier predicted
      # exactly this shape and the mutant was inspected.
      echo "  FAIL: $label — MUTANT DID NOT COMPILE, so it proves nothing"
      grep -E 'error|parse error|FS[0-9]{4}' "$md/compile.log" 2>/dev/null | head -3 | sed 's/^/      /'
      FAILED=1
    elif [ -z "$after" ]; then
      # THE COMPILE GUARD ABOVE WAS ONLY HALF A GUARD. A mutant that compiles
      # and then CRASHES — a malformed regex built at module init, say — prints
      # no rows at all, and an ABSENT row compares unequal to the pinned value
      # exactly as a changed one does. The verifier built such a mutant and drove
      # it through this judging block to produce `killed ... -> ` with an empty
      # right-hand side. A kill requires a row that is PRESENT and DIFFERENT.
      echo "  FAIL: $label — mutant produced no '$row' row (it did not run); proves nothing"
      tail -3 "$md/out" 2>/dev/null | sed 's/^/      /'
      FAILED=1
    elif [ "$after" != "$before" ]; then
      echo "  killed  $label — $row: ${before#*|} -> ${after#*|}"
    else
      echo "  FAIL: $label SURVIVED — $row still reads '$after'"
      FAILED=1
    fi
  fi
  i=$((i + 1))
done

if [ "$FAILED" -eq 0 ]; then
  echo "FSX-PRELUDE PROOF: java string and regex semantics reproduced, the exhaustive 65536-code-unit differential matches, and seven planted divergences killed"
else
  echo "FSX-PRELUDE PROOF FAILED"
  exit 1
fi
