# OCaml Maven lifecycle POC on Luigi

Date: 2026-09-30 (America/Chicago). Deployment:
`/home/srikanth/fogell-ocaml-poc-2026-09-30` on Luigi. The deployed source
archive SHA-256 is `b9b7df9885cc666590a64f19837d6818583be54eea115731c8590651e4be0be9`;
the native OCaml executable SHA-256 is
`38a469e063e1fe06249b8316b08ff44af9f3a16fc7ef6f53fd6f4e1d0d7bd07d`.

## Result

Seven serial, alternating, warmed, **clean package** trials on the same fixture:

| Tool | Wall times (seconds) | Median | Range | Median maximum RSS* |
| --- | --- | ---: | ---: | ---: |
| Maven 3.8.7 | 3.58, 3.82, 3.80, 3.54, 3.80, 3.80, 3.60 | **3.80 s** | 3.54–3.82 s | 318 MiB |
| Native OCaml POC | 2.18, 2.17, 2.33, 2.26, 2.18, 2.29, 2.28 | **2.26 s** | 2.17–2.33 s | 114 MiB |

The POC's median is **1.54 s lower (40.5%)**, or **1.68× as fast**, on this
small clean build. Median user CPU time was 13.73 s for Maven and 5.82 s for
the POC; median system CPU time was 1.25 s and 0.63 s. CPU seconds can exceed
wall seconds on this multicore host.

Both tools compiled the same three main and three test Java sources at release
17, ran the same **10 passing JUnit 4 tests**, copied the same resource, and
packaged the same four checked payload entries. SHA-256 hashes of the three
`.class` files and the resource matched between tools in every trial. The
complete JARs are not byte-identical because Maven adds its own metadata.

## Method

- Host: Luigi, Ubuntu Linux 6.8.0-138-generic, dual Intel Xeon E5-2695 v3
  (28 physical cores, 56 threads), 125 GiB RAM. JDK: Ubuntu OpenJDK
  `javac 25.0.4.1`. OCaml: 4.14.1 native executable rebuilt on Luigi with a
  private compiler toolchain in the deployment directory.
- The Maven online build first fetched dependencies and plugins. Timed Maven
  runs used `mvn -o -B -ntp -q clean package`; timed POC runs used
  `./mini_mvn --project fixture clean package`. Each tool had one unmeasured
  warmup from the benchmark harness. Trials then alternated tool order.
- GNU `/usr/bin/time` measured each process tree. The harness checked the
  Surefire XML count or `JUnitCore` output after every clean build, and read
  the built JAR to verify the expected payload hashes. The one-minute load
  average over the measured trials rose from roughly 1.5 to 3.4 on 56 logical
  CPUs.
- Raw per-trial wall, CPU, RSS, load, test, and payload data are in
  [results.json](results.json). The [raw archive](raw-results.tar.gz) includes
  command logs and GNU `time` output (SHA-256
  `9d6da7bd057737bc954ae90143bae4317475d804e1f0763c76b83c90cede1b88`).
  The executable fixture and harness are in
  [the experiment](../../experiments/ocaml-maven-poc/README.md).

\* GNU `time` maximum RSS is a peak for a process in the command tree, not the
sum of all concurrent processes' resident memory. Treat it as an indicative
process footprint, not total build memory consumption.

## Interpretation

The measured difference is real for this controlled, small, cached Java
module, but it does not isolate OCaml language speed. Both paths still use
the JDK compiler and a JVM test runner; the POC also starts the `jar` tool.
Maven performs POM/model processing, plugin lifecycle setup, Surefire test
reporting, and richer JAR metadata. The POC implements only literal direct
dependencies already in the local repository, one module, JUnit 4 class-name
discovery, resource copying, and four lifecycle goals. It has no parent POM,
transitive resolution, profiles, plugin semantics, network fetch, install, or
deploy. Its simpler scope accounts for part of the speedup.

The result supports pursuing a fast native coordinator for frequent small
agent builds. A useful next gate is a real multi-module project with cold and
warm dependency resolution, incremental changes, and deploy steps; this POC
cannot yet execute that workload fairly.
