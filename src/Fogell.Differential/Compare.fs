namespace Fogell.Differential

open System
open System.IO
open System.Security.Cryptography

/// Why a comparison failed, named so a report is machine-readable.
type Divergence =
    | ResultDiffers of jenkins: string * fogell: string
    | DiagnosticSilence of engine: string
    | OutputDiffers of firstMismatchIndex: int * jenkins: string option * fogell: string option
    | WorkspaceDiffers of jenkins: string * fogell: string
    | JenkinsFailed of string
    | FogellFailed of string

    member this.Describe =
        match this with
        | ResultDiffers(j, f) -> $"terminal result: jenkins={j} fogell={f}"
        | DiagnosticSilence engine -> $"{engine} failed without reporting a reason"
        | OutputDiffers(i, j, f) ->
            let show = Option.defaultValue "<absent>"
            $"output line {i}: jenkins={show j} fogell={show f}"
        | WorkspaceDiffers(j, f) -> $"workspace hash: jenkins={j.Substring(0, 12)} fogell={f.Substring(0, 12)}"
        | JenkinsFailed e -> $"jenkins side failed: {e}"
        | FogellFailed e -> $"fogell side failed: {e}"

/// The verdict for one file. ADR 0001's tiers, decided by evidence.
type Verdict =
    /// Same result and same output. Whether the WORKSPACE was also compared is
    /// carried separately, because a receipt that says "same workspace" when no
    /// workspace was collected is precisely the vacuous claim this harness
    /// exists to prevent.
    | Proven
    /// Ran on both, but they disagree. NOT a Fogell bug by assumption — the
    /// divergence is named so it can be adjudicated.
    | Diverged of Divergence list
    /// One side could not run it at all.
    | NotComparable of Divergence

type Receipt =
    { File: string
      Verdict: Verdict
      JenkinsCore: string
      Jenkins: Trace option
      Fogell: Trace option
      ComparisonContract: string list
      /// Hash over the receipt's own comparable content, so a receipt cannot be
      /// edited after the fact without detection.
      Seal: string }

module Compare =

    let private sha256Text (text: string) =
        use h = SHA256.Create()

        Text.Encoding.UTF8.GetBytes text
        |> h.ComputeHash
        |> Convert.ToHexString
        |> fun s -> s.ToLowerInvariant()

    /// FG-036. Ordering relaxation for parallel runs, stated rather than hidden.
    ///
    /// Two concurrent branches produce their lines in whatever order the OS
    /// scheduler chose. Comparing that as a SEQUENCE would make the receipt a
    /// race: it would pass or fail on scheduling, not on semantics. So for a
    /// pipeline containing a `parallel` block, output is compared as a sorted
    /// MULTISET — every line must still be present, exactly as many times — but
    /// the interleaving is not compared.
    ///
    /// MEASURED: declarative Jenkins does NOT prefix branch output with
    /// `[branchName]`. That prefix belongs to the SCRIPTED `parallel` map form.
    /// An earlier version of Fogell emitted it and produced a divergence on every
    /// parallel case; the fix was to stop inventing attribution Jenkins does not
    /// provide. It also means the relaxation genuinely loses information — with
    /// no prefix, a line cannot be attributed to a branch at all — which is why
    /// the parallel receipts lean on the WORKSPACE hash for their real claim.
    let private compareOutput (concurrent: bool) (jenkins: string list) (fogell: string list) =
        let rec walk i j f =
            match j, f with
            | [], [] -> None
            | jh :: jt, fh :: ft ->
                if jh = fh then walk (i + 1) jt ft
                else Some(OutputDiffers(i, Some jh, Some fh))
            | jh :: _, [] -> Some(OutputDiffers(i, Some jh, None))
            | [], fh :: _ -> Some(OutputDiffers(i, None, Some fh))

        if concurrent then
            walk 0 (List.sort jenkins) (List.sort fogell)
        else
            walk 0 jenkins fogell

    /// Compare two traces. Workspace hashes are only compared when BOTH sides
    /// collected one — claiming a match against "not-collected" would be exactly
    /// the sort of vacuous pass this harness exists to prevent.
    /// True only when BOTH sides produced a real workspace hash.
    let workspaceWasCompared (jenkins: Trace) (fogell: Trace) =
        jenkins.WorkspaceHash <> "not-collected" && fogell.WorkspaceHash <> "not-collected"

    let traces (jenkins: Trace) (fogell: Trace) : Verdict =
        let divergences =
            [ if jenkins.Result <> fogell.Result then
                  ResultDiffers(jenkins.Result, fogell.Result)

              match compareOutput (jenkins.Concurrent || fogell.Concurrent) jenkins.Output fogell.Output with
              | Some d -> d
              | None -> ()

              // A FAILED or ABORTED build must be explained by both engines.
              // `unstable` is excluded deliberately: Jenkins marks a build
              // unstable from a test report and prints no ERROR line — the report
              // is the explanation. Requiring one there fired symmetrically on
              // both engines, which is the signature of a wrong rule rather than
              // a real divergence.
              if jenkins.Result = "failure" || jenkins.Result = "aborted" then
                  if not fogell.ReportedFailureReason then DiagnosticSilence "fogell"
                  if not jenkins.ReportedFailureReason then DiagnosticSilence "jenkins"

              if
                  jenkins.WorkspaceHash <> "not-collected"
                  && fogell.WorkspaceHash <> "not-collected"
                  && jenkins.WorkspaceHash <> fogell.WorkspaceHash
              then
                  WorkspaceDiffers(jenkins.WorkspaceHash, fogell.WorkspaceHash) ]

        if List.isEmpty divergences then Proven else Diverged divergences

    let receipt (file: string) (core: string) (jenkins: Result<Trace, string>) (fogell: Result<Trace, string>) : Receipt =
        let verdict, j, f =
            match jenkins, fogell with
            | Result.Error e, _ -> NotComparable(JenkinsFailed e), None, None
            | _, Result.Error e -> NotComparable(FogellFailed e), None, None
            | Result.Ok jt, Result.Ok ft -> traces jt ft, Some jt, Some ft

        let comparable =
            let render (t: Trace option) =
                match t with
                | Some x ->
                    let joined = String.concat "\n" x.Output
                    $"{x.Result}|{x.WorkspaceHash}|{joined}"
                | None -> "<none>"

            $"{file}\n{core}\n{render j}\n{render f}"

        { File = file
          Verdict = verdict
          JenkinsCore = core
          Jenkins = j
          Fogell = f
          ComparisonContract =
            Trace.comparisonContract
            @ (if [ j; f ] |> List.choose id |> List.exists (fun t -> t.Concurrent) then
                   [ "PARALLEL: output compared as a sorted multiset, not a sequence — concurrent"
                     "  branches interleave nondeterministically, so a sequence comparison would"
                     "  pass or fail on OS scheduling. Line content and multiplicity ARE compared;"
                     "  order is NOT, and declarative Jenkins emits no branch prefix to attribute"
                     "  a line by. The load-bearing claim for these cases is the workspace hash." ]
               else
                   [])
          Seal = sha256Text comparable }

    /// Render a receipt as text. Deliberately plain so it can be committed,
    /// diffed and hashed alongside the code.
    let render (r: Receipt) : string =
        let sb = Text.StringBuilder()
        let line (s: string) = sb.AppendLine s |> ignore

        line $"# Differential receipt — {r.File}"
        line ""
        line $"jenkins-core: {r.JenkinsCore}"
        line $"seal:         {r.Seal}"
        line ""

        let workspaceCompared =
            match r.Jenkins, r.Fogell with
            | Some j, Some f -> workspaceWasCompared j f
            | _ -> false

        match r.Verdict with
        | Proven when workspaceCompared ->
            line "VERDICT: PROVEN (tier 1) — same result, same output, same workspace hash"
        | Proven ->
            line "VERDICT: PROVEN-PARTIAL — same result, same output."
            line "  WORKSPACE NOT COMPARED: no workspace was collected from at least one side,"
            line "  so this is NOT a tier-1 claim under ADR 0001. See FG-002b."
        | Diverged ds ->
            line $"VERDICT: DIVERGED ({ds.Length})"

            for d in ds do
                line $"  - {d.Describe}"
        | NotComparable d ->
            line "VERDICT: NOT COMPARABLE"
            line $"  - {d.Describe}"

        line ""
        line "## Comparison contract"

        for c in r.ComparisonContract do
            line $"  {c}"

        let renderSide name (t: Trace option) =
            line ""
            line $"## {name}"

            match t with
            | None -> line "  (did not run)"
            | Some x ->
                line $"  result:         {x.Result}"
                line $"  workspace-hash: {x.WorkspaceHash}"
                line $"  output ({x.Output.Length} lines):"

                for l in x.Output do
                    line $"    | {l}"

                if not (List.isEmpty x.WorkspaceFiles) then
                    line "  workspace:"

                    for path, hash in x.WorkspaceFiles do
                        line $"    {hash.Substring(0, 12)}  {path}"

        renderSide "Jenkins" r.Jenkins
        renderSide "Fogell" r.Fogell
        sb.ToString()

    let seal (directory: string) (r: Receipt) : string =
        Directory.CreateDirectory directory |> ignore
        let safe = r.File.Replace("/", "_").Replace(".Jenkinsfile", "")
        let path = Path.Combine(directory, $"{safe}.receipt.txt")
        File.WriteAllText(path, render r)
        path
