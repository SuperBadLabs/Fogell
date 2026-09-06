# Three-engine standoff over six real OSS projects — 2026-09-06, mario

Fogell `e60cde34` vs Jenkins 2.568.1 vs McLoving, each building the same
bounded target in six pinned open-source projects. 36 builds, no failures.

## Result

Per-project medians, ms, 2 heats each:

| project | Fogell | Jenkins | McLoving |
|---|---:|---:|---:|
| jenkinsci/git-plugin | 66,117 | 73,182 | **51,356** |
| apache/activemq | 37,280 | 39,787 | **30,549** |
| apache/cxf | 79,689 | 79,224 | **64,923** |
| apache/dubbo | 72,991 | 74,481 | **59,859** |
| apache/maven | 67,429 | 71,983 | **56,176** |
| apache/camel | 122,183 | 121,273 | **95,927** |
| **total** | 445,689 | 459,930 | **358,790** |
| **vs Jenkins** | 1.03× | 1.00× | **1.28×** |

**McLoving wins 6 of 6**, between 1.22× and 1.42× on every project. Fogell
ties Jenkins — four narrow wins, two sub-1% losses.

### This inverts the microbenchmark

`bench/faceoff/` measured per-stage machinery with `sh 'true'`: Fogell 27.7×
Jenkins, McLoving 1.7×. On real builds the ranking reverses completely.

The gap is not per-stage cost. McLoving leads Fogell by ~14.5 s per build over
three stages; Fogell's measured per-stage advantage is 374 ms. **The real-work
gap is ~40× larger than anything stage count can explain.** The trivial-step
benchmark measured the one dimension where Fogell dominates and which
contributes almost nothing to a real build.

## Fairness controls

- **Matched pipelines from one generator.** A Jenkinsfile (Fogell + Jenkins)
  and a McLoving YAML per project, carrying byte-identical shell command text;
  only the outer wrapper differs per format. The generator asserts no `"`, `$`
  or `\` appears in a command, since either wrapper would mangle those.
- **Per-project JDK from the project's own declaration**, enforcer **NOT**
  skipped. ActiveMQ declares `requireJavaVersion [24,)` ("we leverage MRJAR")
  and builds on JDK 25; the rest on 21.
- Offline Maven, Develocity build cache disabled, `clean package` per heat,
  fresh Jenkins job per heat, engine order rotated per (round, project).
- No CPU/memory cap on the Jenkins container.

## Deviations

Tests, spotbugs, checkstyle, javadoc, rat and source jars are skipped. These
change build duration, not whether the compiled output is the project's real
output. `-Denforcer.skip` was used in a first probe and **withdrawn** — see below.

## Two void runs, kept deliberately

**`standoff-VOID-quoting-bug.log` — 24 of 36 runs failed.** The generator wrote
`sh 'find ... -path '*/target/*.jar' ...'`: single quotes inside a single-quoted
Groovy string. McLoving's YAML passes the same text as a double-quoted scalar,
so only McLoving was unaffected — the failure looked exactly like "McLoving is
the only engine that works". The driver's guard refused to print a ratio.

**An earlier probe passed `-Denforcer.skip=true`.** That flag disables
`requireJavaVersion`, i.e. each project's own statement of what it needs.
ActiveMQ would have refused JDK 21 and said why; gagged, it would have produced
a multi-release JAR without its multi-release classes and reported success.
Withdrawn; every build here runs with the enforcer live.

Both defects share a shape worth naming: **a setup error that yields plausible
output rather than an obvious crash.** The guard, not the measurement, is what
caught them.

## Incidental differential finding — Fogell over-accepts

The malformed Jenkinsfile above is an accidental differential case, and Fogell
and Jenkins disagree on it:

| engine | behaviour |
|---|---|
| Jenkins 2.568.1 | refuses at `CompilationUnit.compile`; **zero stages run** |
| Fogell `e60cde34` | parses it; runs `Toolchain` ✓ and `Build` ✓ (19.7 s of Maven, `clean` deleting artifacts); fails only at `Verify` |

Fogell executed two stages of a pipeline real Jenkins will not start. This is
over-acceptance **with side effects**, and it inverts the fail-closed doctrine:
Jenkins fails safe and total, Fogell does partial work on an invalid file. In a
migration it is silent. The ledger has no case for it. It arose from a realistic
authoring mistake, which makes it a better case than most hand-written ones.

## Log-volume test — INCONCLUSIVE, hypothesis refuted

`logvolume-inconclusive.log`. Same builds with Maven stdout to `/dev/null`:

| engine | noisy | quiet | Δ |
|---|---:|---:|---:|
| Jenkins | 66,927 | 48,776 | −18,151 |
| Fogell | 62,715 | 64,009 | +1,294 |
| McLoving | 53,586 | 70,066 | +16,480 |

**Fogell does not improve when output is suppressed**, so log handling does not
explain its deficit — hypothesis refuted. McLoving getting 16 s *slower* with
less work is directionally impossible, so this run's noise exceeds its signal
(n=2 per cell on a drifting box) and the Jenkins figure is not trustworthy
either. Recorded as a null result. **What still explains the real-work gap is
not established, and should be found by profiling rather than by proposing
mechanisms one at a time.**

## Limits

Two heats per project per engine; mario drifts within a session. The
**ordering** is what survives — McLoving first on all six, Fogell level with
Jenkins — not the individual medians. McLoving ran at
`MCLOVING_POLL_MILLISECONDS=10`, not the shipped 500; negligible at this
timescale but the rig is still not measuring it as shipped.

**McLoving may be faster because it promises less.** Fogell's per-step durable
exactly-once journal is its stated product; McLoving's persistence guarantees
were not characterised here. Wall clock across engines with unequal durability
guarantees is not a like-for-like comparison, and 1.28× should not be read as
McLoving beating Fogell at the same job until those guarantees are compared.
