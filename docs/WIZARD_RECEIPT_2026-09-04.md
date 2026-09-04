# Fogell wizard receipt — 2026-09-04 — identities of the tenure's publications

Companion to [`WIZARD_HANDOFF_2026-09-04.md`](WIZARD_HANDOFF_2026-09-04.md).
Every row names an exact head, its merge into protected `main`, the hosted
pull-request run that passed all ten jobs on that head, and the exact-head
review coverage `scripts/review-coverage.py` accepted. Times are UTC on
2026-09-03/04. Superseded PRs are listed because their reviews shaped the
head that merged.

## Source and accounting publications

| ticket | PR | head | merge | hosted run (pull_request) | Copilot review | Codex |
|---|---|---|---|---|---|---|
| FG-205 source | #373 | `a0aa81b6fa1fc253001883bab71c0e64bd4e8ae6` | `1a14803203e29950a1ead89367d2955e07425a5c` 23:34:58Z | `33817405001` 23:23:38Z–23:31:07Z | `5107735814` changes recommended (three comments: two declined, one folded later) | clean `5533424921` |
| FG-205 accounting | #375 | `08d7088cf17f09e62655e32cf649bf7ba4df8ddc` | `2f2af43a4f704cfc73e42446087b44b3df3da31b` 23:52:26Z | `33818817511` 23:43:30Z–23:50:10Z | `5107829944` approval recommended | clean `5533600229` |
| FG-140 accounting (+ FG-241 filed) | #376 | `e2a9c13d1a733cacc3e2d20e2e9a14e8125928c2` | `9d83e3c8dac19e83d5c464d280eda4f25d466318` 05:20:45Z | `33839611139` 05:12:06Z–05:18:14Z | `5109415847` approval recommended | clean `5536031583` |
| FG-241 source | #381 (#378 superseded, head `7b387229`) | `787c6fc375459cfa5fb5ffa5425f71f3420f4ab1` | `0ba672156dfb765c47a4a30dac2262a0a1948ac8` 06:35:24Z | `33844357466` 06:26:24Z–06:34:00Z | `5109881833` changes recommended (two comments: one declined, one folded into accounting) | clean `5536629275` |
| FG-241 accounting | #385 (#383 superseded on a Codex P2: acceptance not yet met) | `a828b94c1c7380d3f81dc97fe15b232c13db7e79` | `db35bd116a20847ae27feb2ac708a8335d5ce8d9` 07:19:44Z | `33847543006` 07:10:43Z–07:18:23Z | `5110221977` approval recommended | clean `5537049932` |
| FG-243 source (+ FG-241 `when` receipt) | #391 (#388, #390 superseded on Codex findings) | `0d4bb765962cc198330e15b838e9fd337af85b2b` | `7a75e5bddf014ba2df64e914f8aff8484204719a` 10:13:06Z | `33861453484` 10:04:30Z–10:11:13Z | `5111669295` approval recommended | clean `5538928334` |
| FG-243 + FG-241 accounting | #393 | `7fe42d094d712a8582bfd74d88baa3e592a2b83b` | `b5209ab9be11772989022d608e985a48623d228f` 10:36:54Z | `33863364649` 10:28:00Z–10:35:55Z | `5111850293` approval recommended (one layout nit declined) | clean `5539169883` |
| FG-133 source | #400 | `545df79229843af35e7e0e61cf22677f2589c75e` | `41c6058b955e1e0c47b315aac18bca85dfabb78b` 13:25:37Z | `33877175532` 13:16:46Z–13:23:52Z | `5113423339` approval recommended (one pre-existing-code nit declined) | clean `5541021541` |
| FG-133 accounting | #404 (#403 superseded on a Codex chronology correction) | `89749c9bced56abd7cfb1c66db667081547fa43f` | `a31dd9939d8649d272d2848aa1875b99a438928f` 14:12:05Z | `33880828330` 13:56:09Z; `hang-proof` flaked on arm `container-stop-hangs` and passed on `--failed` re-run | `5113854754` approval recommended (two prose nits declined) | clean `5541507165` |

Every merge listed has the head's tree as its own tree and a GitHub-verified
signature (checked at each merge with `git log -1 --format='%T'` against
`git rev-parse <head>^{tree}` and the commits API). Every merge was made with
`--match-head-commit` on the exact reviewed head.

## Local gates

Each merged head passed the unmodified `scripts/build-and-test.sh` on HeMan
against a fresh PostgreSQL 16 container with the FG-094 corpus baseline and
Jenkins oracle fixtures, final `OK`: FG-205 `a0aa81b6` 23:16:54Z–23:23:21Z;
FG-205 accounting `08d7088c` 23:36:21Z–23:43:00Z; FG-140 `e2a9c13d`
05:05:22Z–05:11:43Z; FG-241 `787c6fc3` 06:19:09Z–06:25:48Z; FG-241 accounting
`a828b94c` 07:03:15Z–07:09:59Z; FG-243 `0d4bb765` 09:57:28Z–10:04:05Z; FG-243
accounting `7fe42d09` 10:20:58Z–10:27:38Z; FG-133 `545df792`
13:09:51Z–13:16:22Z; FG-133 accounting `89749c9b` 13:49:21Z–13:55:43Z. Two
gate runs of earlier FG-241 cuts were killed by a peer session's cleanup
(acknowledged over session messages) and re-run.

## Verifier rounds

A read-only adversarial verifier round with an explicit verdict preceded
the first push of every source head and of the FG-205, FG-241, FG-243 and
FG-133 accounting heads; folds were applied before that push, and the
replacement pushes that followed review-bot findings carried only those
folds. The NOT SAFE verdicts that became folds were: FG-205 rounds 1 and 2 (corpus `.sort(` claim,
stale `Ordered` comment, DONE before gate, a stale quote); FG-241 round 1 P3s
(dates, a stale bullet, a 72-line count); FG-241 accounting (a Copilot verdict
mis-cited, unlogged gate passes); FG-243 rounds 1 and 2 (JDK module frames
rejected by the frame shape; a dropped newline; a limit the code lacked);
FG-243 accounting (a Copilot verdict mis-cited); FG-133 accounting (a hold
sentence that overstated). Each source head's round is recorded in its ticket's Review findings
section; the accounting rounds are recorded here and in the FG-205 and
FG-133 tickets, and FG-140 (accounting only) had its round before #376.

## Receipts added this tenure

`fg205-cyclic-map-sort`, `fg241-regex-pattern-fault`,
`fg243-when-regex-fault-uncaught`, `compile-refusal-option-unknown-pipeline`,
`compile-refusal-option-unknown-stage`,
`compile-refusal-option-pipeline-only-at-stage` — all tier-1 PROVEN; 306
seals verify at handoff; scorecard 301 of 301 expected cases.

## Board at handoff

`rows=237; DONE=180; open=57; open P0–P3=2 / 19 / 31 / 5` —
`scripts/bin/audit-board-numbers` consistent; queue-row audit clean;
compatibility ledger tier1=5, tier3=28, admitted=195 of 228.
