# Rust, OCaml, and Maven on Luigi

Date: 2026-09-30 (America/Chicago). Deployment:
`/home/srikanth/fogell-native-build-comparison-2026-09-30` on Luigi. This is a
fresh three-way run on the exact same Java fixture, not a comparison of runs
from different days.

## Clean build

Nine measured, serial, warm-cache `clean package` trials per tool, with the
order rotated equally among Maven, OCaml, and Rust:

| Tool | Wall times (seconds) | Median | Range | Median maximum RSS* |
| --- | --- | ---: | ---: | ---: |
| Maven 3.8.7 | 3.86, 3.68, 3.70, 3.77, 3.60, 3.74, 3.60, 3.94, 3.35 | **3.70 s** | 3.35–3.94 s | 325 MiB |
| OCaml 4.14.1 | 2.29, 2.18, 2.23, 2.29, 2.19, 2.12, 2.07, 2.21, 2.09 | **2.19 s** | 2.07–2.29 s | 114 MiB |
| Rust 1.97.1 | 2.18, 2.24, 2.07, 2.33, 2.28, 2.02, 2.10, 2.16, 2.13 | **2.16 s** | 2.02–2.33 s | 114 MiB |

Against Maven, Rust saved **1.54 s at the median (41.6%)** on this small
fixture; OCaml saved 1.51 s (40.8%). The Rust and OCaml medians differ by
0.03 s, and their ranges overlap. Nine trials do not support a meaningful
full-build winner between them. Median user CPU time was 13.69 s for Maven,
5.68 s for OCaml, and 5.69 s for Rust. Both native programs launch the same JDK
compiler, JUnit runner, and `jar` tool; those processes dominate this build.

Every measured build passed the same **10 JUnit 4 tests** and produced identical
SHA-256 hashes for the three compiled classes and packaged resource. The
complete JARs are not compared byte-for-byte; Maven also creates richer
metadata and Surefire reports.

## Coordinator-only clean

With `target` already absent, 2,000 alternating invocations per native tool
measured process startup, literal POM and cached dependency handling, and a
no-op `clean`. No Java tool or JAR packaging ran:

| Tool | Median invocation | p95 invocation |
| --- | ---: | ---: |
| OCaml | 3.093 ms | 3.524 ms |
| Rust | **2.093 ms** | **2.363 ms** |

Rust was **1.000 ms (32.3%) faster at the median** and faster in 1,824 of
2,000 alternating pairs. These are end-to-end process invocations measured
from Python, so they include OS spawn and scheduling. They are useful for
frequent cheap agent operations, but say little about builds dominated by
`javac` and tests.

The deployed Rust executable is 616,112 bytes; the OCaml executable is
1,788,616 bytes. These are executable sizes only, with shared system libraries
outside the files.

## Method and limits

- Luigi: dual Intel Xeon E5-2695 v3, 28 physical cores/56 threads, Ubuntu
  24.04, kernel 6.8.0-138-generic, 125 GiB RAM. JDK: `javac 25.0.4.1`.
  Rust was built on HeMan using `cargo build --release --offline` with
  `rustc 1.97.1` and copied to Luigi. The OCaml 4.14.1 binary had been built
  and verified on Luigi.
- Maven first had its dependencies and plugins cached. Measured Maven command:
  `mvn -o -B -ntp -q clean package`. Native commands used their respective
  executable with `--project fixture clean package`. Each tool had one
  unmeasured warmup before nine measured trials. The harness rotated tool
  order across each group of three trials and checked tests and JAR payloads
  after the timed command. The one-minute load average before measured trials
  rose from roughly 0.5 to 2.4 on 56 logical CPUs.
- The native POCs only implement a single-module subset with literal direct
  dependencies already cached locally, JUnit 4 class-name discovery,
  resource copying, and four lifecycle goals. They do not implement parent
  POMs, transitive resolution, profiles, plugins, multi-module builds,
  network fetch, install, or deploy. Maven's fuller work accounts for part
  of the gap; this is not a general Maven replacement result.
- [Full-build data](results.json) and [no-op clean samples](clean-results.json)
  contain every measurement. The [raw archive](raw-results.tar.gz) contains
  all logs and GNU `time` files (SHA-256
  `bcf1d7343fc3c4e7da6a92b8827f88ade229f3137701f11a5ad029ee972705e3`).
  The Rust [source and harness](../../experiments/rust-maven-poc/README.md)
  are in the repo; Luigi's Rust source archive SHA-256 is
  `b151ab385076c0322d2de297b580eec6a334aac9c015f8bbf108fed8b1e90c5d`.
  The timed Rust executable SHA-256 is
  `316dbcb12bd1fbf99064153043c391819eecbf968b0062a52cda4f6814e6af2b`.

\* GNU `time` maximum RSS is the largest process peak in the command tree,
not total concurrent build memory.
