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

The ten historical rows still open after this slice are HariSekhon, jjasghar,
maajor, maxyermayank, metersphere, mjah, MrRameshRajendran, rosineygp, SumitKr88
and yashpimple. Each needs its own
minimal repro and measured row transition; their reported positions below are
not diagnoses. FG-015 is not part of this implementation and is closure-audited
separately.

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
