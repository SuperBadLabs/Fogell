#!/usr/bin/env python3
"""FG-198. The queue-row rule, enforced by a script instead of by whoever is looking.

The rule (docs/EXECUTION_BOARD.md, "Live queue"): a queue row may state the measured
SYMPTOM and its RANKING ARGUMENT. It must not name a CAUSE, predict a FIX, size a
SCOPE, or make a claim that could become false if the table were reordered with no
code changing. Nineteen instances across two shapes survived sincere manual sweeps
before this script existed; three of the last four were introduced by the patches
fixing the others, which is the measurement that hand-editing does not converge.

WHAT THIS CHECKS, exactly: the last cell ("why it is next") of every row in the
three Track tables under "## Live queue", after stripping quoted spans, against a
deny-list of overclaim tells in two families:
  - cause/fix/scope: "no <x> change", "self-contained", "if real is", "fires on",
    "all <x> pipelines", "the fix is", "trivial", "one-line", "simply"
  - reorder-fragile: "Follows FG-<n>", "head of the", "this position",
    "highest-ranked", "every <...> row/ticket", "every class-<letter>"

QUOTED SPANS ARE EXEMPT: a retraction quoting what a row USED to say is the honesty
this board practises, and flagging it would punish exactly the rows that corrected
themselves. Double-quoted spans and backticked spans are stripped; single-quoted
spans are stripped ONLY when both quotes sit at word boundaries, because a bare
apostrophe in `FG-183's` defeats naive quote-pairing — the first version of this
checker flagged a retraction for precisely that reason, and the proof lane keeps a
fixture for it.

WHAT IT CANNOT DO, stated so a pass is not misread: a deny-list catches SPELLINGS,
not overclaims. It is a floor, not the rule — a cause named in words it does not
know passes silently, and a row that passes has NOT been proven compliant. Same
honesty the FG-162 audit prints about prose numbers.

usage: scripts/audit-queue-rows.py [board-file]
       The optional path exists for prove-queue-rows.sh, which runs this against
       mutated scratch copies; a checker never proven to fail is indistinguishable
       from a broken one.
"""

import re
import sys
from pathlib import Path

BOARD = Path(sys.argv[1]) if len(sys.argv) > 1 else (
    Path(__file__).resolve().parent.parent / "docs" / "EXECUTION_BOARD.md"
)

# Each entry: (name, compiled pattern). Case-insensitive throughout — the board
# bolds with capitals, and an overclaim in shouting is still an overclaim.
TELLS = [
    ("scope: 'no <x> change'", re.compile(r"\bno\s+\w+\s+change\b", re.I)),
    ("fix: 'self-contained'", re.compile(r"\bself-contained\b", re.I)),
    ("scope: 'if real is'", re.compile(r"\bif\s+real\s+is\b", re.I)),
    ("scope: 'fires on'", re.compile(r"\bfires\s+on\b", re.I)),
    ("scope: 'all <x> pipelines'", re.compile(r"\ball\s+\w+\s+pipelines\b", re.I)),
    ("fix: 'the fix is'", re.compile(r"\bthe\s+fix\s+is\b", re.I)),
    ("fix: 'trivial'", re.compile(r"\btrivial\b", re.I)),
    ("fix: 'one-line'", re.compile(r"\bone-line\b", re.I)),
    ("fix: 'simply'", re.compile(r"\bsimply\b", re.I)),
    ("positional: 'Follows FG-<n>'", re.compile(r"\bfollows\s+FG-\d+", re.I)),
    ("positional: 'head of the'", re.compile(r"\bhead\s+of\s+the\b", re.I)),
    ("positional: 'this position'", re.compile(r"\bthis\s+position\b", re.I)),
    ("positional: 'highest-ranked'", re.compile(r"\bhighest-ranked\b", re.I)),
    # the two UNIVERSAL instances quantified over rows/tickets; "every stage" or
    # "every receipt" are ordinary prose and must not fire
    ("universal: 'every … row/ticket'", re.compile(r"\bevery\s+(?:\w+\s+){0,2}(?:rows?|tickets?)\b", re.I)),
    ("universal: 'every class-<letter>'", re.compile(r"\bevery\s+class-[A-E]\b", re.I)),
]

# Order matters: backticks first (a backticked span may contain quotes), then
# double quotes, then boundary-anchored single quotes. Lookarounds keep an
# apostrophe inside a word (FG-183's) from opening or closing a span.
STRIP = [
    re.compile(r"`[^`]*`"),
    re.compile(r'"[^"]*"'),
    re.compile(r"(?<!\w)'([^']*?)'(?!\w)"),
]

# The replacement is a bracketed sentinel, NOT a bare space: substituting a space
# merges the words flanking two adjacent quoted spans, and the FG-201-cycle
# verifier planted `said the "…" fix "…" is withdrawn` and watched the audit flag
# the seam as `the fix is`. `[q]` is non-word text, so no \b-anchored tell can
# match across it, and it cannot itself complete a tell.
QUOTE_SENTINEL = " [q] "


def unquoted(text: str) -> str:
    for pat in STRIP:
        text = pat.sub(QUOTE_SENTINEL, text)
    return text


def queue_rows(lines, tracks_seen):
    """Yield (line_no, row_id, why_cell) for every data row of the Track tables.

    Records each Track heading in tracks_seen: a track whose table vanishes
    silently shrinks coverage, and the first proof run showed exactly that —
    renaming Track 1 left Tracks 2-3 parseable and the audit passed on rows it
    was no longer checking all of.
    """
    in_queue = False
    current_track = ""
    for i, line in enumerate(lines, start=1):
        m = re.match(r"^### Track ([123]) ", line)
        if m:
            tracks_seen.add(m.group(1))
            current_track = m.group(1)
            in_queue = True
            continue
        # any heading that is not a Track heading ends the queue region
        if in_queue and re.match(r"^#{2,3} ", line):
            in_queue = False
            continue
        if not in_queue or not line.startswith("|"):
            continue
        # Split on UNESCAPED pipes only: `\|` is literal text inside a Markdown
        # table cell, and splitting on it truncated the scanned cell at the
        # escape — a one-character evasion of every tell, found by the
        # FG-201-cycle verifier planting `the fix is … \| and the rest`.
        cells = [c.strip() for c in re.split(r"(?<!\\)\|", line.strip().strip("|"))]
        if len(cells) < 3:
            continue
        # header and separator rows
        if cells[-1] in ("why it is next", "") or set(cells[0]) <= {"-", ":", " "}:
            continue
        row_id = next((c for c in cells if "FG-" in c), cells[0])
        # strip markdown emphasis so \b anchors see the words, not asterisks
        why = cells[-1].replace("**", "")
        yield i, row_id, why, current_track


def main() -> int:
    if not BOARD.is_file():
        print(f"audit-queue-rows: no board at {BOARD} — refusing to report a vacuous pass")
        return 2

    lines = BOARD.read_text(encoding="utf-8").splitlines()
    tracks_seen: set = set()
    rows = list(queue_rows(lines, tracks_seen))

    # rows are required PER TRACK, not in aggregate: a heading whose table
    # vanished would otherwise be vouched for by the other tracks' rows
    # (Codex, PR #94 head review — the same shape the proof's renamed-heading
    # arm covers only when heading AND table go together)
    rows_by_track: dict = {}
    for _, _, why, track in rows:
        rows_by_track[track] = rows_by_track.get(track, 0) + 1

    if tracks_seen != {"1", "2", "3"} or any(rows_by_track.get(k, 0) == 0 for k in ("1", "2", "3")):
        # A board with a missing track table is a parse failure, not a clean
        # board — the FG-158 shape (a checker reading absence as approval).
        missing = sorted({"1", "2", "3"} - tracks_seen)
        print(f"audit-queue-rows: track table(s) {missing or 'all'} not found — "
              "the table moved or the parser broke; refusing to pass")
        return 2

    findings = []
    for line_no, row_id, why, _ in rows:
        text = unquoted(why)
        for name, pat in TELLS:
            m = pat.search(text)
            if m:
                findings.append((line_no, row_id, name, m.group(0)))

    print(f"queue-row audit: {len(rows)} rows scanned in {BOARD.name}")
    if findings:
        print(f"\n{len(findings)} overclaim tell(s) in queue rows — each names a cause, fix, scope,")
        print("or a fact the # column already owns. Move it to the ticket row or delete it:\n")
        for line_no, row_id, name, matched in findings:
            print(f"  {BOARD.name}:{line_no}  {row_id}  [{name}]  …{matched}…")
        return 1

    print("no tells matched. FLOOR, NOT PROOF: this deny-list catches known spellings;")
    print("a cause named in words it does not know passes silently.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
