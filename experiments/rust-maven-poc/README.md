# Rust build lifecycle POC

This is a dependency-free Rust counterpart to the
[OCaml POC](../ocaml-maven-poc/README.md). It accepts the same `--project`,
`--repo`, `clean`, `compile`, `test`, and `package` arguments and uses the same
fixture. It reads literal direct dependencies from a small POM subset, invokes
`javac`, runs `*Test.java` classes with JUnit 4, copies resources, and invokes
`jar`. It deliberately has the same limits as the OCaml POC: no parent POMs,
transitive resolution, network fetches, profiles, plugins, multi-module
builds, install, or deploy.

Build with Rust 1.97 or later using only the standard library:

```sh
RUSTC_WRAPPER= cargo build --release --offline
target/release/mini_mvn_rust --project ../ocaml-maven-poc/fixture clean package
```

`benchmark.py` expects the sibling `ocaml-maven-poc` directory and a
`mini_mvn_rust` executable in this directory (or `target/release`). It runs
Maven, OCaml, and Rust serially on that fixture, rotating each tool through
first, second, and third position. By default it takes one warmup per tool
and nine measured runs per tool. Every trial checks ten passing tests and
hashes the same three class files and resource from the resulting JAR. The
comparison report and raw data are under `reports/`.

`benchmark_clean.py` separately times alternating native `clean` invocations
with no `target` directory. It measures coordinator startup and POM handling,
without Java subprocesses. Both measurements and their limits are described
in the [Luigi report](../../reports/rust-ocaml-maven-luigi-2026-09-30/REPORT.md).
