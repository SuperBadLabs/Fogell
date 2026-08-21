# Fogell day-1 backlog — the 14 files Forge rejects

## FG-014 Fogell rebaseline — 2026-08-18

The list below is historical Forge evidence, not the current Fogell baseline. At
the start of FG-014, Fogell admitted 183 of the pinned 228 files and rejected 44;
one further file was tier 1. Two of the 14 historical rows already admitted:
`ljpengelen_jenkinsfile.Jenkinsfile` and
`murphysecurity_murphysec-jenkins-tools.Jenkinsfile`. Twelve remained, so the old
acceptance target of 205 admitted was impossible from this ticket's own scope:
even moving all twelve would produce only 195.

The first measured slice adds the valid Declarative command form in `tools { }`.
The corpus, rather than the historical list, names its full movement: six files
move from tier 3 to admitted — `beifei1_fire-cloud.Jenkinsfile`,
`hungbang_spring-boot-aws-docdb-example.Jenkinsfile`,
`Rapter1990_springbootmicroservicedailybuffer.Jenkinsfile`,
`buildit_jenkins-pipeline-libraries.Jenkinsfile`,
`pavankjadda_BookStore.Jenkinsfile` and `sidd-harth_apigee-cicd.Jenkinsfile`.
Each contains the same isolated construct, a tool kind followed by its quoted
installation name. Admission therefore moves 183 -> 189 and tier 3 moves
44 -> 38. `SumitM01_CI-CD-for-Docker-Kubernetes-using-Jenkins.Jenkinsfile` and
`holdennguyen_cicd-pipeline-java-webapp.Jenkinsfile` pass their `tools` section
and reach a later rejection; they do not move verdict class. No other corpus
verdict changes.

This movement remains exactly what the ledger calls it: parse-only admission.
Selections are retained at pipeline and stage scope, but execution currently refuses
any non-empty `tools` section before workspace preparation or effects. Fogell does not
yet resolve configured installation names, provision tools on an agent, merge scope,
or inject tool-specific `PATH`, `JAVA_HOME` or Maven home variables. That runtime work
is a separate follow-on dependency; silently inheriting similarly named host binaries
would be a false success, not partial tool support.

The second slice covers a separate construct accepted by direct Jenkins 2.568.1
model-converter probes: a structural Declarative section carrying one quoted
display label. `steps('Collect All Mesh')`, `post('Notification')` and
`stages("mkdkr_exporter")` share that exact grammar. The label is not retained in
the Declarative model, while each section body is. The three matching rows —
`maajor_Blender-Geometry-CI.Jenkinsfile`,
`metersphere_jenkins-plugin.Jenkinsfile` and `rosineygp_mkdkr.Jenkinsfile` — move
from tier 3 to admitted. Admission therefore moves 189 -> 192 and tier 3 moves
38 -> 35, with no other corpus verdict change. Broader structural-section
argument shapes remain refused rather than being skipped as unchecked Groovy.

The third slice covers one general argument construct: a named value delimited
by balanced square brackets. Lists and maps share the same syntax boundary, and
the existing raw scanner could not distinguish their inner commas and braces
from separators in the enclosing call. Direct Jenkins 2.568.1 probes accept the
isolated map- and list-valued forms. The complete corpus movement is five rows:
`fatimajamali81_jenkins-iis-cicd-pipeline.Jenkinsfile`,
`holdennguyen_cicd-pipeline-java-webapp.Jenkinsfile`,
`k11h-de_zap-jenkins.Jenkinsfile`,
`maxyermayank_jenkins-pipeline-demo-api.Jenkinsfile` and
`nikoly_selenium-grid-docker.Jenkinsfile` move from tier 3 to parse-only
admission. Admission therefore moves 192 -> 197 and tier 3 moves 35 -> 30.
`SumitKr88_multiscanpipeline-jenkins-fastlane-ios.Jenkinsfile`,
`cloudogu_gitops-playground.Jenkinsfile` and
`jerearista_python-jenkinsfile-testing.Jenkinsfile` pass this construct and
reach later rejections; they do not move verdict class. No other corpus verdict
changes.

Those five movements are not executable-compatibility claims. Exact whole-file
model-converter probes accept `fatimajamali81` and `maxyermayank`; `holdennguyen`
reaches the lab's missing Maven-installation configuration, while `k11h-de_zap`
contains a literal placeholder and `nikoly` carries an independently invalid
Declarative step shape. The scorecard records parsing only and does not erase
those separate facts. At runtime every newly admitted named collection is
refused by the shared execution preflight before workspace preparation or any
effect, until a step's list/map semantics are implemented and proven. Existing
positional collection semantics such as `withEnv(['A=1'])` remain executable.

Six historical rows remain after oracle-preserving minimisation and parser
instrumentation: HariSekhon reaches a closure-valued named argument (and later a
chained-call named value); jjasghar a closure/string-key `parallel` call whose
trailing comma alone is not the blocker; mjah an inline named Kubernetes stage
agent; MrRameshRajendran a GString property name; SumitKr88 now passes its
list-valued `choice` and reaches a later environment rejection; and yashpimple
is rejected by Jenkins itself for an unterminated quoted URL. These are
isolated-probe diagnoses, not guesses from ledger error positions. FG-015 is not
part of this implementation and is closure-audited separately.

Measured 2026-07-30 via real `forge validate` dispatch over the pinned
228-file corpus. 214/228 accepted (93.9%). All 14 failures are on the
typed Declarative path (`Forge.Pipeline.Parser`, 995 lines).

| file | path | first error |
|---|---|---|
| `beifei1_fire-cloud.Jenkinsfile` | declarative | parse error at line 52, col 20: Error in Jenkinsfile: Ln: 52 Col: 20 |
| `HariSekhon_Jenkins.Jenkinsfile` | declarative | parse error at line 1078, col 13: Error in Jenkinsfile: Ln: 1078 Col:  |
| `hungbang_spring-boot-aws-docdb-example.Jenkinsfile` | declarative | parse error at line 14, col 41: Error in Jenkinsfile: Ln: 14 Col: 41 |
| `jjasghar_jenkinsfile_cookbook_pipeline.Jenkinsfile` | declarative | parse error at line 49, col 40: Error in Jenkinsfile: Ln: 49 Col: 40 |
| `ljpengelen_jenkinsfile.Jenkinsfile` | declarative | parse error at line 53, col 27: Error in Jenkinsfile: Ln: 53 Col: 27 |
| `maajor_Blender-Geometry-CI.Jenkinsfile` | declarative | parse error at line 5, col 19: Error in Jenkinsfile: Ln: 5 Col: 19 |
| `maxyermayank_jenkins-pipeline-demo-api.Jenkinsfile` | declarative | parse error at line 64, col 18: Error in Jenkinsfile: Ln: 64 Col: 18 |
| `metersphere_jenkins-plugin.Jenkinsfile` | declarative | parse error at line 38, col 9: Error in Jenkinsfile: Ln: 38 Col: 9 |
| `mjah_kubernetes-jenkins-cicd-pipeline-example.Jenkinsfile` | declarative | parse error at line 49, col 51: Error in Jenkinsfile: Ln: 49 Col: 51 |
| `MrRameshRajendran_Hybrid_MultiCloud_Overlay.Jenkinsfile` | declarative | parse error at line 170, col 4: Error in Jenkinsfile: Ln: 170 Col: 25  |
| `murphysecurity_murphysec-jenkins-tools.Jenkinsfile` | declarative | parse error at line 22, col 66: Error in Jenkinsfile: Ln: 22 Col: 66 |
| `rosineygp_mkdkr.Jenkinsfile` | declarative | parse error at line 6, col 9: Error in Jenkinsfile: Ln: 6 Col: 9 |
| `SumitKr88_multiscanpipeline-jenkins-fastlane-ios.Jenkinsfile` | declarative | parse error at line 11, col 40: Error in Jenkinsfile: Ln: 11 Col: 40 |
| `yashpimple_Jenkins-CI-CD-with-GitHub-Integration.Jenkinsfile` | declarative | parse error at line 32, col 1: Error in Jenkinsfile: Ln: 32 Col: 1 |

Note: `HariSekhon_Jenkins.Jenkinsfile` is Jenkins upstream's own
Jenkinsfile; `jenkins_Jenkinsfile` in the user's tree is the same file.
Bisect method: minimal repros via `probe3.fsx`, never the reported
error position — FParsec reports where the longest parse stopped.
