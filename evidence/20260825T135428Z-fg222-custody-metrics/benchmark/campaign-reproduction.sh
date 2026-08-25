#!/usr/bin/env bash
# Faithful single-script reconstruction of the successful Fogell FG-222 HeMan campaign.
# The original campaign was issued in phases; its raw outputs are preserved beside this file.
set -euo pipefail

REPO=/home/srikanth/projects/fogell-worktrees/custodian-fg222-port
HEAD=20ec7cbc8171be8dd7b0e6f1c2df15e7abe65406
OUT=$(mktemp -d /tmp/fogell-fg222-bench-20ec7cb.XXXXXX)
mkdir -p "$OUT/src" "$OUT/logs" "$OUT/cases" "$OUT/runs"

git -C "$REPO" archive "$HEAD" | tar -x -C "$OUT/src"
{
  hostname
  date -Ins
  uname -a
  lscpu
  free -h
  df -T "$OUT"
  git --version
  dotnet --info
} > "$OUT/host-toolchain.txt" 2>&1
git -C "$REPO" show -s --format=fuller "$HEAD" > "$OUT/commit.txt"
git -C "$REPO" status --branch --short > "$OUT/git-status-before.txt"

/usr/bin/time -f 'elapsed_s=%e\nuser_s=%U\nsys_s=%S\nmaxrss_kb=%M\nexit=%x' \
  -o "$OUT/restore-slnx.time" \
  dotnet restore "$OUT/src/Fogell.slnx" --locked-mode \
  > "$OUT/logs/restore-slnx.log" 2>&1
/usr/bin/time -f 'elapsed_s=%e\nuser_s=%U\nsys_s=%S\nmaxrss_kb=%M\nexit=%x' \
  -o "$OUT/build-slnx.time" \
  dotnet build "$OUT/src/Fogell.slnx" -c Release --no-restore \
  > "$OUT/logs/build-slnx.log" 2>&1
sha256sum "$OUT/src/tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host" \
  > "$OUT/run-host.sha256"

cd "$OUT/src"
/usr/bin/time -f 'elapsed_s=%e\nuser_s=%U\nsys_s=%S\nmaxrss_kb=%M\nexit=%x' \
  -o "$OUT/fg222-proof-warmup.time" \
  ./scripts/prove-control-env-isolation.sh > "$OUT/logs/fg222-proof-warmup.log" 2>&1
printf 'rep\telapsed_s\tuser_s\tsys_s\tmaxrss_kb\texit\n' > "$OUT/fg222-proof-times.tsv"
for i in $(seq 1 15); do
  /usr/bin/time -f "$i\t%e\t%U\t%S\t%M\t%x" -o "$OUT/fg222-proof-$i.time" \
    ./scripts/prove-control-env-isolation.sh > "$OUT/logs/fg222-proof-$i.log" 2>&1
  cat "$OUT/fg222-proof-$i.time" >> "$OUT/fg222-proof-times.tsv"
  grep -Fqx 'FG-222 controller environment proof: PASS' "$OUT/logs/fg222-proof-$i.log"
done

CASES="$OUT/cases"
printf "%s\n" \
  "pipeline {" \
  "  agent any" \
  "  stages {" \
  "    stage('one') { steps { echo 'bench-mark-line' } }" \
  "  }" \
  "}" > "$CASES/echo-1stage.Jenkinsfile"
{
  printf "%s\n" \
    "pipeline {" \
    "  agent any" \
    "  stages {" \
    "    stage('ladder') { steps {"
  for i in $(seq 1 200); do printf "      sh 'true'\n"; done
  printf "%s\n" "    } }" "  }" "}"
} > "$CASES/sh-ladder-200.Jenkinsfile"
{
  printf "%s\n" \
    "pipeline {" \
    "  agent any" \
    "  stages {" \
    "    stage('fan') {" \
    "      parallel {"
  for b in $(seq 0 7); do
    printf "        stage('b%s') { steps {\n" "$b"
    for m in $(seq 1 10); do printf "          sh 'true'\n"; done
    printf "        } }\n"
  done
  printf "%s\n" "      }" "    }" "  }" "}"
} > "$CASES/parallel-8x10.Jenkinsfile"
sha256sum "$CASES"/*.Jenkinsfile > "$OUT/case-sha256.txt"

uptime > "$OUT/load-before.txt"
BIN="$OUT/src/tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host"
run_one() {
  local case_name=$1 case_file=$2 rep=$3 table=$4
  local root="$OUT/runs/$case_name/$rep"
  mkdir -p "$root"
  /usr/bin/time -f "$rep\t%e\t%U\t%S\t%M\t%x" -o "$root/time.tsv" \
    "$BIN" "$case_file" "$root/ws" job "$root/build.journal" \
    > "$root/run.log" 2>&1
  cat "$root/time.tsv" >> "$table"
  test "$(awk -F '\t' '$1 == "build-finished" { count++; value=$2 } END { print count ":" value }' \
    "$root/build.journal")" = "1:success"
}

: > "$OUT/warmup-durable-times.tsv"
for i in 1 2 3; do
  run_one warmup "$CASES/echo-1stage.Jenkinsfile" "$i" "$OUT/warmup-durable-times.tsv"
done
printf 'rep\telapsed_s\tuser_s\tsys_s\tmaxrss_kb\texit\n' > "$OUT/echo-durable-times.tsv"
for i in $(seq 1 15); do
  run_one echo "$CASES/echo-1stage.Jenkinsfile" "$i" "$OUT/echo-durable-times.tsv"
done

: > "$OUT/per-step-warmup.tsv"
run_one per-step-warmup "$CASES/echo-1stage.Jenkinsfile" 1 "$OUT/per-step-warmup.tsv"
printf 'rep\telapsed_s\tuser_s\tsys_s\tmaxrss_kb\texit\n' > "$OUT/per-step-base-times.tsv"
for i in $(seq 1 8); do
  run_one per-step-base "$CASES/echo-1stage.Jenkinsfile" "$i" "$OUT/per-step-base-times.tsv"
done
printf 'rep\telapsed_s\tuser_s\tsys_s\tmaxrss_kb\texit\n' > "$OUT/sh200-durable-times.tsv"
for i in $(seq 1 8); do
  run_one sh200 "$CASES/sh-ladder-200.Jenkinsfile" "$i" "$OUT/sh200-durable-times.tsv"
done

: > "$OUT/parallel-warmup.tsv"
run_one parallel-warmup "$CASES/echo-1stage.Jenkinsfile" 1 "$OUT/parallel-warmup.tsv"
printf 'rep\telapsed_s\tuser_s\tsys_s\tmaxrss_kb\texit\n' > "$OUT/parallel-8x10-durable-times.tsv"
for i in $(seq 1 15); do
  run_one parallel "$CASES/parallel-8x10.Jenkinsfile" "$i" "$OUT/parallel-8x10-durable-times.tsv"
done
uptime > "$OUT/load-after.txt"
git -C "$REPO" status --branch --short > "$OUT/git-status-after.txt"

python3 - "$OUT" <<'PY'
import csv, json, math, statistics, sys
from pathlib import Path
out = Path(sys.argv[1])
files = {
    "fg222_proof": "fg222-proof-times.tsv",
    "durable_echo_e2e": "echo-durable-times.tsv",
    "durable_echo_base": "per-step-base-times.tsv",
    "durable_sh200": "sh200-durable-times.tsv",
    "durable_parallel_8x10": "parallel-8x10-durable-times.tsv",
}
def summarize(name):
    rows = list(csv.DictReader((out / files[name]).open(), delimiter="\t"))
    elapsed = [float(r["elapsed_s"]) for r in rows]
    rss = [int(r["maxrss_kb"]) for r in rows]
    ordered = sorted(elapsed)
    percentile = lambda p: ordered[min(len(ordered)-1, math.floor(p * len(ordered)))]
    mean = statistics.mean(elapsed)
    sd = statistics.stdev(elapsed) if len(elapsed) > 1 else 0.0
    return {
        "n": len(elapsed),
        "failures": sum(int(r["exit"]) != 0 for r in rows),
        "elapsed_s": {
            "median_harness": percentile(0.5),
            "mean": mean,
            "stdev_sample": sd,
            "cv_percent": 100 * sd / mean,
            "p95_harness": percentile(0.95),
            "min": min(elapsed),
            "max": max(elapsed),
        },
        "maxrss_kb": {
            "median_harness": sorted(rss)[math.floor(0.5 * len(rss))],
            "min": min(rss),
            "max": max(rss),
        },
    }
summary = {name: summarize(name) for name in files}
base = summary["durable_echo_base"]["elapsed_s"]["median_harness"]
ladder = summary["durable_sh200"]["elapsed_s"]["median_harness"]
summary["durable_sh200"]["marginal_ms_per_step_from_medians"] = \
    (ladder - base) * 1000.0 / 200.0
(out / "metrics-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
PY

printf 'campaign output: %s\n' "$OUT"
