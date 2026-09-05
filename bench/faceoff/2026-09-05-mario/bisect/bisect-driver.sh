#!/usr/bin/env bash
# Round-robin bisect driver. Chronological candidate order; each round runs the
# list in the opposite direction so monotone drift on the box cancels instead of
# landing entirely on the builds measured last.
set -Eeuo pipefail
ROOT=$HOME/faceoff2
CANDS=$ROOT/cands
OUT=$ROOT/out/bisect
LINK=$ROOT/fogell/net10.0
ORDER=(76072354 b5f9edf3 de684547 66904473 16982e1a 793b565e ac3e934e e60cde34)
ROUNDS=3
HEATS=5

mkdir -p "$OUT"
rm -f "$OUT"/*.tsv

for r in $(seq 1 "$ROUNDS"); do
  if (( r % 2 == 0 )); then
    list=()
    for (( i=${#ORDER[@]}-1; i>=0; i-- )); do list+=("${ORDER[$i]}"); done
  else
    list=("${ORDER[@]}")
  fi
  echo "=== round $r  (order: ${list[*]}) ==="
  for sha in "${list[@]}"; do
    [ -f "$CANDS/$sha/Fogell.Run.Host.dll" ] || { echo "!! missing $sha"; exit 1; }
    ln -sfn "$CANDS/$sha" "$LINK"
    line=$(cd "$ROOT" && python3 faceoff.py --engines fogell --sizes 50,100 \
             --heats "$HEATS" --out "$OUT/$sha-r$r.tsv" 2>&1 \
           | rg -N "^  fogell  " || true)
    printf "  r%d %-10s %s\n" "$r" "$sha" "${line:-NO RESULT}"
  done
done
ln -sfn "$CANDS/e60cde34" "$LINK"
echo "=== done; engine link restored to e60cde34 ==="
