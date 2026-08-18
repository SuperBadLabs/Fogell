#r "/home/srikanth/.nuget/packages/fparsec/1.1.1/lib/netstandard2.0/FParsecCS.dll"
#r "/home/srikanth/.nuget/packages/fparsec/1.1.1/lib/netstandard2.0/FParsec.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Domain.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Ir.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Admission.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Groovy.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Groovy.Parser.dll"

open System.IO
open Fogell.Groovy.Parser

// First-bad-line probe: the longest prefix that parses once its open braces
// are auto-closed marks the line where the grammar actually stops. Brace
// balance is maintained, so the unbalanced-`}` degeneration of plain ddmin
// cannot happen. Lines inside strings/comments can fool the brace counter;
// results are printed with context for a human check, not trusted blind.
let parses (src: string) =
    match Parser.parse src with Ok _ -> true | Error _ -> false

let autoClose (lines: string[]) =
    let text = String.concat "\n" lines
    // crude brace count outside line comments; block comments/strings ignored
    let mutable depth = 0
    for line in lines do
        let noComment =
            match line.IndexOf "//" with
            | -1 -> line
            | i -> line.[.. i - 1]
        for ch in noComment do
            if ch = '{' then depth <- depth + 1
            elif ch = '}' then depth <- depth - 1
    if depth > 0 then text + "\n" + String.replicate depth "}\n" else text

let firstBad (lines: string[]) =
    // binary search the smallest n where prefix n fails (monotone enough in
    // practice; verified linearly around the answer)
    let bad n = not (parses (autoClose lines.[.. n - 1]))
    let mutable lo, hi = 1, lines.Length
    if not (bad lines.Length) then None
    else
        while lo < hi do
            let mid = (lo + hi) / 2
            if bad mid then hi <- mid else lo <- mid + 1
        Some lo

let corpus = "/sn8100/work/exchange/crucible-gate/corpus/jenkinsfiles"

let files = [
    "arun-gupta_docker-jenkins-pipeline.Jenkinsfile"
    "Jotschi_maven-release-workflow-test.Jenkinsfile"
    "jenkinsci_jenkins.Jenkinsfile"
    "cloudogu_ces-build-lib.Jenkinsfile"
    "jenkinsci_docker.Jenkinsfile"
    "merken_netCoreBuild.Jenkinsfile"
    "ricardozanini_soccer-stats.Jenkinsfile"
    "Ableton_python-pipeline-utils.Jenkinsfile"
    "captjt_jenkins-pipeline-express.Jenkinsfile"
    "camiloribeiro_cdeasy.Jenkinsfile"
    "esign-consulting_logistics.Jenkinsfile"
    "jalogut_jenkinsfile-basic-sample.Jenkinsfile"
    "kesselborn_jenkinsfile.Jenkinsfile"
    "microsoft_movie-db-java-on-azure.Jenkinsfile"
    "mraible_ng-demo.Jenkinsfile"
    "cloudogu_reveal.js-docker-example.Jenkinsfile"
    "gdemengin_pipeline-logparser.Jenkinsfile"
    "alexguzun_jenkins-pipeline-gitflow-maven.Jenkinsfile"
    "kishorebhatia_pipeline-as-code-demo.Jenkinsfile"
    "judexzhu_Jenkins-Pipeline-CI-CD-with-Helm-on-Kubernetes.Jenkinsfile"
    "jenkinsci_docker-agents.Jenkinsfile"
    "j8kin_habr-jenkinsfile.Jenkinsfile"
    "jenkinsci_jenkinsfile-runner.Jenkinsfile"
]

for name in files do
    let lines = File.ReadAllLines(Path.Combine(corpus, name))
    match firstBad lines with
    | None -> printfn "%-60s parses OK whole" name
    | Some n ->
        let show i = if i >= 1 && i <= lines.Length then lines.[i - 1].TrimEnd() else ""
        printfn "%-60s first bad line %d: %s" name n (show n)
        printfn "%60s   context -1: %s" "" (show (n - 1))
