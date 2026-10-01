# OCaml build lifecycle POC

`mini_mvn.ml` is a small native OCaml experiment for a single Java module. It reads
the fixture's `pom.xml`, supports `clean`, `compile`, `test`, and `package` goals,
uses locally cached direct dependency JARs, copies resources, runs JUnit 4 tests,
and creates a JAR. Goals can be combined, for example `clean package`.

Build with OCaml 4.14 or later and the standard `unix` and `str` libraries:

```sh
bash build.sh
./mini_mvn --project fixture clean package
```

On Luigi, the benchmark deployment has a private OCaml 4.14.1 toolchain under
`toolchain/`. Rebuild there with:

```sh
CAMLLIB="$PWD/toolchain/usr/lib/ocaml" PATH="$PWD/toolchain/usr/bin:$PATH" bash build.sh
```

The fixture can also be built by Maven:

```sh
cd fixture
mvn -B -ntp clean package
```

The POC intentionally accepts a narrow POM shape: one direct project coordinate
and literal dependency versions, with JARs already in `~/.m2/repository`. The
optional `--repo PATH` changes that local repository path. It discovers test
classes from `*Test.java` and runs them through JUnit 4's `JUnitCore`. It does
not implement Maven parent inheritance, transitive resolution, network fetches,
plugin execution, profiles, multi-module builds, install, or deploy. Its JAR
manifest and metadata can differ from Maven's.

`benchmark.py` performs two warmups and seven alternating pairs of serial,
offline, clean-package builds. Each trial verifies ten passing tests and four
expected JAR payload entries. It saves per-trial logs, GNU `time` records, and
`benchmark-results/results.json`. The comparison measures this fixture and
these implementations; it is not a general Maven compatibility or throughput
claim. See the dated report under `reports/` for the Luigi results.
