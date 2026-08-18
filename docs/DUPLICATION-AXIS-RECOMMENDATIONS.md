---
title: Duplication-axis recommendations
audience: owner
category: engineering-board
purpose: Per-row acceptance-field reading of the open platform rows against McLoving's surveyed code, recommending a bucket per row. Recommendations only — no row carries a tag until the owner decides.
lifecycle: live
last-verified: 2026-08-17
---

# Duplication-axis recommendations — open rows, Waves 5–8 plus five named rows

**Method.** Per the board's own rule ("The duplication axis — added 2026-08-17",
`docs/EXECUTION_BOARD.md` ~line 253): each recommendation below is derived from the row's
**acceptance field**, not its title, and checked against what `docs/MCLOVING-SOURCE-SURVEY.md`
and `docs/COMPARISON-MATRIX.md` (rev. 3) actually establish about McLoving, with each cell's
evidence basis noted (`[S]` source-cited with `file:line`, `[D]` doc-cited, header-depth per
survey §6). The governing rule from the withdrawn table: **a sibling engine having a capability
does not satisfy a ticket whose acceptance is engine-specific** — a differential receipt against
Jenkins, a named threat model discharged for Fogell, or a byte-identical Fogell rebuild cannot
be DUPLICATED regardless of what McLoving has.

**Limits.** (1) The survey read ~15 of 30 crates at `lib.rs`-header depth and grepped the spine
for specific claims; nothing was executed (survey §6). Where a recommendation leans on a cell at
that depth it is flagged provisional below. (2) These are **recommendations only** — the board
says a DUPLICATED row needs a *decision* (build, borrow, or drop), and only the owner converts
rows into decisions. No row is recommended for dropping. (3) When in doubt between DUPLICATED
and DISTINCT, DISTINCT was chosen and the doubt stated — the withdrawn table's 55% error rate is
the cost of the opposite bias.

## Recommendations

| id | wave | pri | status | acceptance (short) | McLoving evidence (survey/matrix citation + depth) | bucket | reasoning |
|---|---|---|---|---|---|---|---|
| FG-062 | 5 | P1 | TODO | Network partition ≥ 45 s costs **zero log lines** — "the Jenkins parity bar" | mTLS mandatory, plaintext impossible `[S]` `bins/controller/src/main.rs:1216` (matrix §5). No cited evidence of agent-local durable log, offset recovery, or partition-loss behaviour; `agent-runtime` surveyed at header depth | **DISTINCT** | Acceptance is a behavioural property of Fogell's agent (adr/0008) under partition, framed as a Jenkins-parity bar. McLoving's mTLS transport is code-cited but nothing cited demonstrates the zero-log-loss-over-45s property, so its work does not demonstrably discharge the acceptance. **Provisional** — see closing section. |
| FG-063 | 5 | P1 | TODO | "Matches Jenkins' behavior" — announce reconnect grace with a budget, then ABORTED with a named cause | Process-group revalidation `[S]` `bins/agent/src/lib.rs:848` — boot-id + start-ticks binding, `RetireStale` (matrix §5) | **DISTINCT** | Already adjudicated by the withdrawn-table review: acceptance requires *announcing a bounded reconnect window*, and matching Jenkins — not process-identity revalidation, which is what McLoving's code-cited mechanism does. Different behaviour; nothing to discharge with. |
| FG-065 | 5 | P2 | TODO | Shim enabled only via env var; limitations documented | Full REST API `[S]` `controller-api/src/lib.rs:311-468`, but its own shape, not Jenkins-shaped (matrix §6) | **DISTINCT** | The acceptance governs a **Jenkins-shaped** facade over Fogell's own controller. McLoving's REST is neither Jenkins-shaped nor a facade over Fogell; nothing it has could be enabled by this env var. |
| FG-066 | 5 | P2 | TODO | Each subcommand (validate/plan/run/build/resume/list/history) has a golden-output test | `mcloving-cli`, 22 subcommands `[S]` `bins/cli/src/lib.rs:236-563` (survey §5) | **DISTINCT** | The subcommands under acceptance operate Fogell builds (run/resume/history); `mcloving-cli` speaks McLoving's REST API and cannot run or resume a Fogell build, so its existing work cannot discharge the golden-output tests. Doubt noted: command design/UX is borrowable, but that informs the build rather than discharging acceptance. |
| FG-067 | 5 | P3 | TODO | Renders a build's stages and log | UI served at `/`, `/app.js`, `/app.css` `[S]` `controller-api/src/lib.rs:466-468`; API has `builds/{id}/logs` and `builds/{id}/graph` routes `[S]` (survey §5, matrix §6) | **DUPLICATED (provisional)** | The acceptance is the only one in this set with **no Fogell-specific evidence demand** — "renders a build's stages and log" names no differential, no threat model, no byte-identity. What would be borrowed: McLoving's served UI assets (`app.js`/`app.css`), adapted to Fogell's API (FG-060/FG-064 already serve status and progressive logs). Provisional because the survey confirms only that a UI is *served*, not what it renders — read `app.js` before acting. |
| FG-072 | 6 | P0 | TODO | Escape-attempt suite fully rejected with named codes | No interpreter to sandbox: matrix §5 sandbox cell is `N/A — no interpreter`; compiler "never evaluates" `[S]` `compiler.clj:133` stops at `CompilePhase/CONVERSION` | **UNIQUE** | The acceptance is a proof suite against Fogell's Groovy interpreter — the runtime Groovy breadth the board names as the one UNIQUE thing. McLoving has no interpreter and provably evaluates no Groovy; there is nothing even adjacent to borrow. |
| FG-073 | 6 | P1 | TODO | Threat model reviewed and committed (untrusted Jenkinsfile, hostile agent, multi-tenant boundary) | McLoving has security *mechanisms* (mTLS `[S]`, RLS `[S]`, secret broker `[S]`) but no cited Fogell-applicable threat model | **DISTINCT** | The board's rule names "a named threat model discharged **for Fogell**" as the canonical engine-specific acceptance — a document about McLoving's boundaries cannot be reviewed-and-committed as Fogell's. The topics are platform topics, hence DISTINCT rather than NEITHER; McLoving's docs are reference material, not discharge. |
| FG-074 | 6 | P1 | TODO | Offline build succeeds from a warm cache | Release provenance / SBOM from `Cargo.lock` `[S]` — for **McLoving's own crates** (survey §3) | **NEITHER** | Acceptance is about Fogell's own repository building offline with pinned deps (.NET/NuGet). That is repo hygiene for this codebase, not platform capability; McLoving's Cargo lockfile audits its own tree and cannot touch Fogell's. |
| FG-080 | 7 | P1 | TODO | Two builds of the same commit produce the same digest | `release-provenance`: signed releases, SBOM, deployment receipts `[S]` (survey §3, matrix §4) | **DISTINCT** | Already adjudicated by the withdrawn-table review: acceptance is **reproducibility evidence — byte-identical Fogell output** — and "signed releases and an SBOM are not that." Byte-identical rebuild of Fogell is the rule's own example of an undischargeable engine-specific acceptance. |
| FG-081 | 7 | P1 | TODO | Forward/back/forward rehearsal; zero FK violations at each phase; logical DB hash identical | McLoving has its own migrations (19 with forced RLS `[S]`) — for its own schema | **DISTINCT** | The rehearsal must run **Fogell's** migrations against Fogell's schema and hash Fogell's DB. McLoving's migration set is evidence about a different schema; nothing there produces the required FK-clean/hash-identical evidence for Fogell. |
| FG-082 | 7 | P1 | TODO | Integrity check clean; one recovery event per build after crash/kill/reboot | Lease-expiry requeue, fence preserved `[S]` `scheduler.rs:563` (matrix §5) | **DISTINCT** | Acceptance is a campaign producing recovery evidence **about Fogell's** builds and children. McLoving's crash/replay machinery is code-cited but recovers McLoving builds; it cannot generate the per-build recovery events or the clean integrity check this row demands of Fogell. |
| FG-083 | 7 | P2 | TODO | N-trigger lanes admit exactly the configured capacity; explicit 503 + `Retry-After` | Trigger ingress, five kinds, PostgreSQL delivery state machine `[S]` `trigger_ingress.rs:18-24` (matrix §4) | **DISTINCT** | Acceptance is a saturation measurement of **Fogell's** queue and admission behaviour under load. McLoving's trigger/queue code is code-cited but its capacity behaviour is a fact about McLoving; adopting it wholesale would be a platform decision, not a discharge of this measurement. |
| FG-084 | 7 | P2 | TODO | ≥ 10 min soak; RSS ceiling and FD range recorded; zero restarts | No cited McLoving soak evidence; survey §6: "nothing here was executed" | **DISTINCT** | Acceptance records resource measurements **of Fogell** under sustained load. No McLoving artifact can supply Fogell's RSS ceiling; the survey moreover establishes that nothing in McLoving was executed by us, so there is not even a McLoving soak result to point at. |
| FG-085 | 7 | P2 | TODO | Restored DB hash matches source, byte-verified | McLoving has a PostgreSQL spine `[S]`; no cited backup/restore procedure | **DISTINCT** | The hash under acceptance is of **Fogell's** database, restored through Fogell's procedure. Nothing cited in the survey covers backup/restore at all, and even if McLoving had one it would restore McLoving's data. |
| FG-163 | 8 | P3 | TODO | (acceptance column: "none") Fix `Compare.fs` global `.Replace` to strip only the terminal extension; update the scorecard-generator mirror in the same commit | No McLoving relevance — Fogell receipt-tooling internal | **NEITHER** | A latent naming defect in Fogell's own receipt writer and its reader. Not platform work; no McLoving cell touches it. |
| FG-165 | 8 | P1 | TODO | Make the daemon start deterministic in CI, skip with a NAMED reason, or fix the `nohup`/reap race in `daemonScript` (`tests/Fogell.Execution.Tests/Tests.fs`) | No McLoving relevance — Fogell test-infrastructure race | **NEITHER** | The acceptance is about Fogell's own containment test establishing its precondition on the GitHub runner, with a named candidate race in `ProcessGroup.fs`/`Tests.fs`. Fogell-specific, not platform. |
| FG-166 | 8 | P3 | TODO | (acceptance column: "none") Carry the source file with each expected name instead of reverse-deriving the case stem in `stale-receipts` | No McLoving relevance — Fogell scripts internal | **NEITHER** | Staleness-check derivation defect in Fogell's own tooling. Not platform work. |
| FG-169 | 8 | P2 | TODO | Hash the receipt text minus delimited unsealed regions; verification becomes "strip the fences, rehash" (hole already closed under FG-161; this row is the simplification) | McLoving seals envelopes (35-file self-excluding manifest `[S]`, survey §1) — over **its own** differential artifacts | **NEITHER** | Acceptance is a redesign of Fogell's own receipt-seal extractor, replacing the line-accounting FG-161 shipped. McLoving's sealing binds McLoving's evidence; its existence cannot delete Fogell's extraction layer. Design precedent at most. |
| FG-173 | 8 | P2 | TODO | Manifest (or second assertion) sees empty directories; manual probe `child/` absent becomes provable | No McLoving relevance cited — Fogell `Trace` checker blind spot | **NEITHER** | A checker blind spot in Fogell's own workspace manifest. Not platform work; nothing in the survey is adjacent. |
| FG-093 | 8 | P1 | TODO | Provenance mismatch fails closed — gate refuses to run on commit/tree/artifact-digest/corpus-manifest mismatch | `release-provenance`: signed releases, SBOM, transparency, deployment receipts `[S]` (survey §3) — header depth | **DISTINCT** | The tuple under acceptance binds **Fogell's** commit, tree, artifact digest and corpus manifest, and the gate that must refuse is Fogell's release gate (born of a Fogell baseline mishap). McLoving's provenance crate signs McLoving releases; it cannot make Fogell's gate fail closed. Provisional only in the weak sense that the crate is header-depth — deeper reading could inform design, not discharge. |
| FG-094 | 8 | P1 | TODO | Front-end acceptance and tier-1 count may never decrease; `vinsguru`-class case (engine rejects what Jenkins rejects) → gate passes | McLoving's admitted surface is ONE job, ONE step mapping `[S]` `catalog.yaml`; `tier1` is a Fogell corpus metric | **UNIQUE** | The quantities the gate protects — Fogell's front-end acceptance (`admitted=168`) and tier-1 count, including Jenkins-negative parity — are measurements of the Groovy breadth that is the board's one UNIQUE asset. McLoving has no comparable population to gate (its compiler admits one job), and a receipt certifies the engine that produced it. |
| FG-026 | 2 | P0 | TODO | Payload substitution rejected; uncertain effects listed for reconciliation (four-state `prepared`/`applied`/`confirmed`/`uncertain` ledger) | Outbox + object store `[S]`; `external-connector` signed single-action receipts + `ShadowReplayer`, `destination-observer` `[S]` (matrix §4/§5) — crates at header depth | **DISTINCT** | This is the board's own worked example of DISTINCT: acceptance demands a **four-state external-effect checkpoint ledger** with immutable payload digests, and the withdrawn-table review already refuted the fenced-attempt-publication match. McLoving's effect machinery is adjacent, not the same state machine. Provisional at the margin — see closing section. |
| FG-028 | 2 | P1 | TODO | Absent context exposes no rows; cross-tenant write rejected | `FORCE ROW LEVEL SECURITY` in **19 migrations** `[S]` e.g. `migrations/0003_public_api.sql:27` (matrix §5) — the strongest code citation in this set | **DISTINCT** | The acceptance properties must hold on **Fogell's** tables under Fogell's transaction-local org setting; McLoving's migrations guard McLoving's schema and cannot expose-or-reject anything in Fogell's DB. This is, however, the cheapest borrow in the set: the forced-RLS + composite-FK technique is directly transplantable and code-cited, so the *build* should start by reading those 19 migrations. Distinct because the technique informs the work; it does not discharge the test. |
| FG-042b | 4 | P2 | TODO | (acceptance column: "requires the controller API (FG-060)") — stable URL, byte-exact retrieval through Fogell's controller | Full artifact API: list, metadata, content download, upload, commit `[S]` `controller-api/src/lib.rs` (survey §5, matrix §6) | **DISTINCT** | The acceptance field itself pins the work to **Fogell's** controller API (FG-060, now DONE) — byte-exactness must be demonstrated for Fogell artifacts served by Fogell's routes. McLoving's download API serves McLoving's store and cannot produce that evidence. Note for the owner: this acceptance field is a dependency note, not a test, and is worth tightening before the work starts. |
| FG-070b | 4 | P1 | TODO | A probe running as the **original user** fails to read both `/proc/<pid>/environ` and the secret file — demonstrated the same way the original exposure was | `secret-broker`: grant/redeem, 15-min TTL, arg/env/file/stdin delivery, zeroized `[S]` (survey §3, matrix §4) | **DISTINCT** | Already adjudicated by the withdrawn-table review: acceptance is **same-UID isolation** proven by a named probe — a different-UID/user-namespace execution change in Fogell — and "a grant/redeem broker with a TTL is a different threat model." The named threat model must be discharged for Fogell. |
| FG-113 | 3.6 | P2 | TODO | Fogell **and** a pinned Jenkins 2.568.1 (identical plugin set) on one Windows host; CRLF normalisation declared in the receipt contract, never quiet; gated on FG-038a (no Windows executor yet) | Real Win32 spawn `[S]` `agent-runtime/src/executor/windows.rs:113`, `windows-job` Job Objects `[S]` `windows-job/src/lib.rs:258`; but **no installer in code** (matrix rev. 3 correction) | **DISTINCT** | Already adjudicated by the withdrawn-table review: this is a **DIFFERENTIAL lane** — its acceptance licenses a Fogell-vs-Jenkins parity claim on Windows, which no amount of McLoving Windows code can license (a receipt certifies the engine that produced it). Side note for FG-038a, the prerequisite: McLoving's `windows-job` Job-Object capsule is a directly relevant, code-cited design to read before replacing `setsid`/`/proc`. |

## Bucket counts

| bucket | count | rows |
|---|---|---|
| UNIQUE | 2 | FG-072, FG-094 |
| DUPLICATED | 1 (provisional) | FG-067 |
| DISTINCT | 17 | FG-062, FG-063, FG-065, FG-066, FG-073, FG-080, FG-081, FG-082, FG-083, FG-084, FG-085, FG-093, FG-026, FG-028, FG-042b, FG-070b, FG-113 |
| NEITHER | 6 | FG-074, FG-163, FG-165, FG-166, FG-169, FG-173 |

The zero-strong-DUPLICATED result is consistent with the board's own state ("No row currently
carries this tag") and with the withdrawn table's lesson: nearly every open platform row's
acceptance demands evidence *about Fogell* — a parity bar, a probe on Fogell's processes, a
hash of Fogell's database, a gate over Fogell's corpus numbers — which existing McLoving code
cannot supply regardless of quality.

## Provisional recommendations — "read that crate properly before acting"

The survey's stated depth limit (§6: ~15 crates at `lib.rs`-header depth, spine grepped,
nothing executed) makes the following provisional:

1. **FG-067 (DUPLICATED)** — the only DUPLICATED recommendation, and it rests on knowing the
   UI is *served* (`controller-api/src/lib.rs:466-468`), not what it renders. Read `app.js`
   before treating the borrow as real; if it does not render stages and logs, this row falls
   back to DISTINCT and the recommendation becomes "build, with McLoving's `logs`/`graph`
   route shapes as reference".
2. **FG-062 (DISTINCT)** — `agent-runtime` was surveyed at header depth. If a proper read
   shows a durable agent-local log with offset recovery that demonstrably survives partition
   without loss, the owner may want to reconsider this row as a borrow candidate — though the
   acceptance property would still need to be demonstrated in Fogell's deployment, so the
   floor is "DISTINCT with a strong design source", not DUPLICATED.
3. **FG-026 (DISTINCT)** — `external-connector` and `destination-observer` are header/doc
   depth. The board has already adjudicated this row as the DISTINCT worked example, so the
   bucket is firm; what is provisional is only how much of the four-state design can be
   cribbed. A deep read that found a prepared/applied/confirmed/uncertain state machine would
   be worth reporting back to the board.
4. **FG-093 (DISTINCT)** — `release-provenance` is header depth; bucket firm (Fogell's gate,
   Fogell's tuple), but read the crate before designing the tuple format.
5. **FG-066 (DISTINCT)** — whether `mcloving-cli` has golden-output tests worth imitating is
   unexamined; bucket firm (its subcommands cannot operate Fogell), design borrow unpriced.

Non-provisional anchors: FG-028's RLS evidence (19 migrations, `file:line`) and FG-113's
Windows executor evidence (`windows.rs:113`, `windows-job/src/lib.rs:258`) are the two
strongest code citations in the McLoving column — the buckets stay DISTINCT for
acceptance-field reasons, but the borrowable *technique* in both is real and code-cited.
