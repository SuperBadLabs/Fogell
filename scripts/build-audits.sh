#!/usr/bin/env bash
# FG-226. Compile the audit tools from `scripts/fsx/*.fsx` to native binaries in
# `scripts/bin/`.
#
# WHY THE BINARIES ARE BUILT AND NEVER COMMITTED. An fflat build is NOT
# reproducible — the same source compiled twice differs by six bytes of embedded
# module GUID (measured 2026-08-27), so "rebuild and compare hashes" cannot prove
# a committed binary matches its source. A committed binary would therefore be an
# unauditable assertion sitting in the blocking gate: edit an .fsx, forget to
# rebuild, and the gate runs the OLD logic and passes green, which is the FG-158
# shape exactly. Building here, in the same run that uses them, makes the
# source-to-binary link true by construction instead of by discipline.
#
# `--check` verifies every tool is present and newer than its source, without
# building. That is the STALENESS guard for a developer who edited an .fsx and
# did not rebuild; the gate calls the plain form and rebuilds unconditionally.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
SRC=scripts/fsx
BIN=scripts/bin

TOOLS=(
  audit-board-numbers
  audit-claims
  audit-stale-refs
  count-options
  generate-scorecard
  probe-input
  review-rounds
  sync-scm-cases
)

if ! command -v fflat >/dev/null 2>&1; then
  echo "FAIL: fflat not on PATH — install with: dotnet tool install -g fflat" >&2
  exit 1
fi

# LINK PREREQUISITES, and why this cannot be an apt package.
#
# bflat invokes its bundled lld with exactly FOUR `-L` paths, all inside fflat's
# own tool store. ld.lld has NO default search directories and ignores
# LIBRARY_PATH (both measured), so installing a system package does not make a
# library resolvable no matter where it lands — it has to be placed in a
# directory bflat already searches.
#
# The fflat 2.1.3 nupkg ships brotli for WINDOWS ONLY: there is no libbrotli*.so
# or .a anywhere in its linux tree. A CLEAN install of the pinned toolchain
# therefore cannot link ANY of these tools on ANY Linux host; the link ends with
#   lld: error: unable to find library -lbrotlienc   (and dec, and common)
#
# That was invisible for months because the authoritative local gate ran on a
# machine whose store carried three symlinks made BY HAND on 2026-05-28, five
# days after it was installed and three months before FG-226 — an undocumented
# patch nobody recorded. It surfaced only on this branch's first hosted run
# (33189551168). Creating them HERE, rather than only in `gate.yml`, is what
# stops the next custodian machine hitting the same opaque failure with the
# remedy buried in a ticket.
#
# Of the 23 `-l` entries on the captured link line (21 distinct names — `gcc`
# and `z` each appear twice), every one but these three resolves from bflat's
# bundled sysroot, so this is the complete missing set for a clean install.
ensure_link_prereqs() {
  local libdir src l fflat_version payload_version fflat_tfm fflat_cmd tool_root
  local -a shim_entries

  # Resolve the version the GLOBAL TOOL SHIM actually embeds. Globbing both
  # version levels made a machine with an old fflat store beside the active one
  # expand to two space-separated paths; `-d` then rejected the combined string
  # even though both directories existed. A global .NET tool apphost carries
  # the relative DLL path it launches; reading that path names the active store
  # version even when stale versions coexist. `dotnet tool list -g` is not an
  # authority here because it can enumerate more than the shim-selected entry.
  fflat_cmd=$(readlink -f "$(command -v fflat)")
  tool_root=$(dirname "$fflat_cmd")
  mapfile -t shim_entries < <(
    LC_ALL=C grep -aoE '\.store/fflat/[0-9A-Za-z._+-]+/fflat/[0-9A-Za-z._+-]+/tools/[^/]+/any/fflat[.]dll' "$fflat_cmd" \
      | sort -u
  )
  if [ "${#shim_entries[@]}" -ne 1 ]; then
    echo "FAIL: expected one embedded fflat DLL path in $fflat_cmd; found ${#shim_entries[@]}" >&2
    return 1
  fi
  fflat_version=$(printf '%s\n' "${shim_entries[0]}" | cut -d/ -f3)
  payload_version=$(printf '%s\n' "${shim_entries[0]}" | cut -d/ -f5)
  fflat_tfm=$(printf '%s\n' "${shim_entries[0]}" | cut -d/ -f7)
  if [ "$fflat_version" != "$payload_version" ]; then
    echo "FAIL: fflat shim store/payload version mismatch: $fflat_version vs $payload_version" >&2
    return 1
  fi
  libdir="$tool_root/.store/fflat/$fflat_version/fflat/$fflat_version/tools/$fflat_tfm/any/lib/linux/x64/glibc"
  if [ -z "$fflat_tfm" ] || [ ! -d "$libdir" ]; then
    echo "FAIL: fflat $fflat_version Linux lib directory not found: $libdir" >&2
    return 1
  fi
  for l in brotlienc brotlidec brotlicommon; do
    [ -e "$libdir/lib$l.so" ] && continue
    # `|| true` is load-bearing: under `set -e -o pipefail` an unmatched glob
    # makes `ls` exit non-zero, pipefail carries that through `head`, and
    # errexit would kill the script AT THE ASSIGNMENT — so the diagnostic
    # below, which is the whole point of the guard, would never print.
    src=$(ls /usr/lib/*/lib"$l".so.1 /lib/*/lib"$l".so.1 2>/dev/null | head -1) || true
    if [ -z "$src" ]; then
      echo "FAIL: lib$l.so.1 is not installed (Debian/Ubuntu: apt-get install libbrotli1)" >&2
      return 1
    fi
    ln -sf "$src" "$libdir/lib$l.so"
    echo "  linked lib$l.so -> $src"
  done
}

# `--preflight` proves the toolchain LINKS, in ~3s, without compiling the eight
# tools. CI runs it straight after installing fflat: the failure it catches
# otherwise surfaces seven minutes in, after the full test suite, because the
# audit build sits at the end of the gate. A trivial .fsx is enough — the brotli
# flags come from the framework link, not from the program, which is why this
# probe genuinely fails when the prerequisites are missing.
if [ "${1:-}" = "--preflight" ]; then
  ensure_link_prereqs
  probe=$(mktemp -d /tmp/fogell-linkprobe.XXXXXX)
  trap 'rm -rf "$probe"' EXIT
  printf '[<EntryPoint>]\nlet main _ = 0\n' > "$probe/probe.fsx"
  if fflat "$probe/probe.fsx" -o "$probe/probe.bin" > "$probe/log" 2>&1; then
    echo "link preflight OK: the audit toolchain links"
    exit 0
  fi
  echo "FAIL: link preflight — the audit tools cannot be compiled on this host" >&2
  tail -20 "$probe/log" >&2
  exit 1
fi

if [ "${1:-}" = "--check" ]; then
  missing=0
  for t in "${TOOLS[@]}"; do
    if [ ! -x "$BIN/$t" ]; then
      echo "  MISSING  $BIN/$t"
      missing=1
    elif [ "$SRC/$t.fsx" -nt "$BIN/$t" ] || [ "$SRC/prelude.fsx" -nt "$BIN/$t" ]; then
      # The prelude is a dependency of every tool, so a prelude edit staleness-
      # marks all of them. Missing that is how one tool keeps old shared
      # semantics while its siblings get the new ones.
      echo "  STALE    $BIN/$t (source is newer)"
      missing=1
    fi
  done
  if [ "$missing" -ne 0 ]; then
    echo "FAIL: audit binaries are missing or stale — run scripts/build-audits.sh" >&2
    exit 1
  fi
  echo "audit binaries current: ${#TOOLS[@]} tools"
  exit 0
fi

ensure_link_prereqs
mkdir -p "$BIN"

# COMPILED IN PARALLEL. Each fflat invocation saturates roughly three cores, and
# there are eight independent tools; run sequentially they cost ~58s of wall time
# on a 32-core host that is idle for most of it. The jobs are capped rather than
# unbounded so this does not thrash a small runner — GitHub's are 4-core, where
# the cap is what keeps it from being SLOWER than sequential.
LOGS=$(mktemp -d /tmp/fogell-build-audits.XXXXXX)
trap 'rm -rf "$LOGS"' EXIT

jobs_max=$(nproc 2>/dev/null || echo 4)
jobs_max=$(( jobs_max / 3 )); [ "$jobs_max" -lt 1 ] && jobs_max=1
[ "$jobs_max" -gt "${#TOOLS[@]}" ] && jobs_max=${#TOOLS[@]}

for t in "${TOOLS[@]}"; do
  while [ "$(jobs -rp | wc -l)" -ge "$jobs_max" ]; do wait -n; done
  {
    # Output is captured per tool and shown only on failure, where it is
    # filtered to genuine compiler diagnostics.
    #
    # AN EARLIER VERSION OF THIS COMMENT JUSTIFIED THAT BY A "wall of IL2xxx/
    # IL3050 trim-analysis warnings out of FSharp.Core on every build". That does
    # not reproduce: measured on this host with the pinned fflat 2.1.3, a
    # successful build of `generate-scorecard`, `review-rounds` and `audit-claims`
    # emits ZERO lines and zero IL warnings. Copilot flagged the comment on PR
    # #181 for naming bflat where the script invokes fflat; the naming is
    # defensible (fflat wraps bflat) but the premise was not, and the decline
    # first written for it repeated the unmeasured claim. The capture is still
    # right — a parallel build interleaves eight streams — so only the reason
    # changed.
    if fflat "$SRC/$t.fsx" -o "$BIN/$t" >"$LOGS/$t.log" 2>&1; then
      : > "$LOGS/$t.ok"
    fi
  } &
done
wait

built=0
for t in "${TOOLS[@]}"; do
  if [ -e "$LOGS/$t.ok" ]; then
    built=$((built + 1))
  else
    echo "FAIL: $t did not compile" >&2
    tail -20 "$LOGS/$t.log" >&2
    exit 1
  fi
done
echo "built $built audit tools into $BIN/"
