# F# build lifecycle POC

This F# counterpart to the [OCaml](../ocaml-maven-poc/README.md) and
[Rust](../rust-maven-poc/README.md) POCs accepts the same `--project`, `--repo`,
`clean`, `compile`, `test`, and `package` arguments. It uses the same checked
Java fixture, literal local JAR dependencies, `javac`, JUnit 4, and `jar`.
The scope is one module with no parent POMs, transitive resolution, profiles,
plugins, network fetch, install, or deploy.

Build both .NET 10 distribution modes:

```sh
dotnet publish FSharpMavenPoc.fsproj -c Release -r linux-x64 \
  -p:PublishAot=false -p:SelfContained=false -p:NuGetAudit=false \
  -o publish/managed
dotnet publish FSharpMavenPoc.fsproj -c Release -r linux-x64 \
  -p:PublishAot=true -p:SelfContained=true -p:NuGetAudit=false \
  -o publish/aot
```

The managed build requires a .NET 10 runtime on the host. The Native AOT
build is a native executable; the .NET 10 FSharp.Core assembly emits AOT
analysis warnings during publication, so the benchmark verifies the actual
executable against all ten tests. The POC avoids F# formatted printing because
one of its runtime generic paths failed in Native AOT.

`benchmark.py` compares Maven, OCaml, Rust, managed F#, and Native AOT F#
serially on the sibling `ocaml-maven-poc/fixture`. It rotates tool order,
verifies ten tests and the same JAR payload hashes after every trial, and
saves logs and GNU `time` records. `benchmark_clean.py` separately measures
coordinator startup and no-op `clean` without Java subprocesses. See the
dated [Luigi report](../../reports/fsharp-native-build-luigi-2026-09-30/REPORT.md)
for results and limits.
