# F# Maven lifecycle POC on Luigi

Date: 2026-09-30 (America/Chicago). Deployment:
`/home/srikanth/fogell-native-build-comparison-2026-09-30/fsharp-maven-poc`
on Luigi. The F# source implements the same narrow `clean`, `compile`, `test`,
and `package` lifecycle as the OCaml and Rust POCs.

## Clean build

Ten measured, serial, warm-cache `clean package` trials per tool. Order rotated
equally through Maven, OCaml, Rust, managed F#, and F# Native AOT. Medians below
use the conventional average of the middle two values:

| Tool | Median wall time | Range | Median maximum RSS* |
| --- | ---: | ---: | ---: |
| Maven 3.8.7 | **3.755 s** | 3.55–4.02 s | 322 MiB |
| OCaml 4.14.1 | **2.270 s** | 2.13–2.33 s | 113 MiB |
| Rust 1.97.1 | **2.235 s** | 2.14–2.33 s | 114 MiB |
| F# managed (.NET 10) | **2.315 s** | 2.25–2.43 s | 114 MiB |
| F# Native AOT | **2.220 s** | 2.16–2.30 s | 113 MiB |

F# Native AOT saved **1.535 s (40.9%)** against Maven on this fixture. Managed
F# saved **1.440 s (38.3%)**. Native AOT's full-build median was 95 ms below
managed F#; the OCaml, Rust, and AOT ranges overlap, so this run does not
establish a full-build winner among the native implementations.

Every warmup and timed build ran the same **10 passing JUnit 4 tests** and
produced identical SHA-256 hashes for the three compiled class files and one
resource. Complete JAR files can differ in Maven metadata and manifests.

## Coordinator-only clean

With `target` absent, 500 balanced, alternating invocations per native/.NET
tool measured process startup, literal POM and local dependency handling, and
a no-op `clean`. No `javac`, JUnit, or `jar` process ran:

| Tool | Median invocation | p95 invocation |
| --- | ---: | ---: |
| Rust | **2.013 ms** | 2.354 ms |
| OCaml | 3.112 ms | 3.568 ms |
| F# Native AOT | 5.892 ms | 7.456 ms |
| F# managed | 69.791 ms | 76.911 ms |

Native AOT reduced F#'s median no-op invocation by **63.899 ms (11.8×)**.
This startup difference matters most for many tiny agent actions; in the full
build, Java compilation and tests dominate all four POCs.

## Method and limits

- Luigi: dual Intel Xeon E5-2695 v3, 28 physical cores/56 threads, Ubuntu
  24.04, Linux 6.8.0-138-generic, 125 GiB RAM. JDK: `javac 25.0.4.1`.
  System SDK: .NET `10.0.112`; managed runtime: `Microsoft.NETCore.App 10.0.12`.
  F# builds were published on HeMan using .NET SDK `10.0.301`, then executed
  and verified on Luigi. The AOT executable needs no .NET runtime on Luigi.
- Maven dependencies and plugins were already cached. Timed Maven command:
  `mvn -o -B -ntp -q clean package`. Native commands used their executable with
  `--project fixture clean package`. Each tool received one unmeasured warmup.
  The harness rotated all five order positions twice and checked test counts
  and JAR payload hashes after each timed build. The one-minute load average
  rose from roughly 1.1 to 3.8 on 56 logical CPUs.
- F# Native AOT publication emitted `FSharp.Core` trim/AOT analysis warnings.
  An initial version failed at runtime in F# formatted printing; the final
  source uses direct `Console.WriteLine` calls. Both final F# executables
  passed the full fixture locally and on Luigi.
- The POCs only support one module, literal direct dependencies already in
  the local cache, JUnit 4 test-class discovery, resource copying, and four
  lifecycle goals. They do not provide parent POM inheritance, transitive
  resolution, profiles, plugins, network fetch, install, or deploy. Maven
  performs more work, so this is not a general Maven replacement result.
- [Full-build data](results.json) and [no-op clean samples](clean-results.json)
  contain every measurement. The [raw archive](raw-results.tar.gz) includes
  logs and GNU `time` records (SHA-256
  `2085afb82d95484668b1b80e55bf06d7ce1208999a5ef04b7c1a31a533d693c9`).
  The [F# source](../../experiments/fsharp-maven-poc/README.md) archive on
  Luigi has SHA-256
  `0271d7583219ba17cb69193b55eb30addd822c8f2133446d4c8a48d2964312ef`.
  The Native AOT executable is
  `2b7610e309ea830f37220ca826f00c4a10000b5d2f3953f9ece0b918e206e136`;
  the managed application DLL is
  `e45770adbc72004ca5a829e28d47f463f6cdf7c2f5a1aeb1727199289890734e`.

\* GNU `time` maximum RSS is the largest process peak in a command tree,
not total concurrent memory used by the build.
