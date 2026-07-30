# Fogell day-1 backlog — the 14 files Forge rejects

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
