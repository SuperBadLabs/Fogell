namespace Fogell.Differential

open System
open Fogell.Domain
open Fogell.Execution

/// FG-105. The walker's run-scoped mutable state — one value constructed per
/// build, passed explicitly where the 2,000-line closure used to capture it.
///
/// Contract, stated once:
///  * this record is the ONLY mutable state a walker unit may touch — a unit
///    that wants a new mutable is asking for a new field, and the review sees
///    the request;
///  * every operation is safe to call from parallel branches: ONE internal
///    lock, held per call. The same lock orders output against secret
///    registration, so a line can never be appended while the masker is
///    unaware of a secret that was bound before it;
///  * `Emit` masks against every secret bound so far — masking is run-scoped,
///    not block-scoped, because a leaked value does not become safe when its
///    `withCredentials` block closes;
///  * the leak scan (`OutputWithActiveSecrets`) judges a line only against
///    secrets bound BEFORE it was emitted: text printed before a value was
///    bound merely coincides with it;
///  * deadline tokens are minted here, per declaration, so expiry ownership
///    can be announced by the scope whose bound actually fired.
type WalkerCtx =
    { /// Append one build-output line, masked against every secret bound so far.
      ///
      /// MEASURED (FG-036): declarative Jenkins emits parallel branch output
      /// with NO `[branchName]` prefix — that belongs to the scripted
      /// `parallel` map form. Fogell emitted one until the receipt said
      /// otherwise, which diverged every parallel case. Inventing attribution
      /// the reference engine does not provide is not a favour, it is a
      /// divergence.
      /// Receipt: `parallel-always-failfast`.
      Emit: string -> unit
      /// Register credential bindings atomically at the CURRENT output index.
      /// Masking applies to every later `Emit`; the recorded index scopes the
      /// LEAK CHECK only — output emitted from here on can leak these values,
      /// output before merely coincides.
      BindSecrets: SecretBinding list -> unit
      /// Locked snapshot of the raw output lines, in emission order.
      Output: unit -> string list
      /// Locked snapshot pairing each line with the secrets that were already
      /// bound when it was emitted — the leak scan's exact input.
      OutputWithActiveSecrets: unit -> (string * SecretBinding list) list
      /// Worst-of accumulator for the build status. Monotone: a later Bump can
      /// only worsen the result, never walk it back (retry uses a throwaway
      /// sink for exactly that reason — see BranchCtx.Sink).
      Bump: BuildStatus -> unit
      Status: unit -> BuildStatus
      /// One clock for the whole build, so a `timeout` deadline is an absolute
      /// point in time rather than a per-step budget.
      RunClock: Diagnostics.Stopwatch
      /// Mint a live deadline with a fresh token. The token is the DECLARING
      /// SCOPE's identity: two scopes can declare the same absolute
      /// millisecond, and nudging the value to disambiguate (the previous
      /// attempt) shifted real execution.
      MkDeadline: int64 -> Deadline
      /// FG-102: record WHICH declared bound actually fired a cancellation.
      /// The exceeded announcer for a scope speaks only when ITS OWN token is
      /// in the fired set — a shared boolean let branch B's normal failure
      /// ride branch A's expiry, and clock arithmetic alone announced timeouts
      /// that never caused anything.
      RecordFired: Deadline option -> unit
      DeadlineDidFire: Deadline option -> bool
      /// FG-103: engine-health notes for the RECEIPT — never build output.
      NoteEngine: string -> unit
      EngineNotes: unit -> string list
      /// The EXACT durable-script ids this run minted, canonicalised by value —
      /// a spoofed id in output stays literal and diverges visibly.
      AddDurableId: string -> unit
      DurableIds: unit -> string list
      /// Groovy's script Binding for THIS build: a placeholder's assignment
      /// outlives its step (receipt `gstring-binding-across-steps`). One per
      /// run — a fresh build starts with a fresh Binding, exactly as Jenkins.
      ScriptBinding: GString.ScriptBinding }

module WalkerCtx =

    /// Build the run-scoped state. Everything mutable lives inside this call's
    /// closures; the returned record is the only handle.
    let create () : WalkerCtx =
        let output = System.Collections.Generic.List<string>()
        // Parallel branches append from several threads at once; this one lock
        // also orders output against secret registration and the fired-set.
        let outputLock = obj ()

        let firedDeadlines = System.Collections.Generic.HashSet<int>()
        let nextDeadlineToken = ref 0

        /// Every secret bound anywhere in this run, each with the output index
        /// it was registered AT. Masking lives HERE, at the one place output is
        /// appended, rather than at each call site.
        ///
        /// The call-site version was reachable: once named arguments began
        /// rendering, `archiveArtifacts artifacts: "${TOKEN}/missing"` put the
        /// credential in the pattern, and `No artifacts found that match the
        /// file pattern "..."` came back as a Diagnostic that the step runner
        /// emitted verbatim. A secret in the build log, produced by a step that
        /// never touches a shell — so masking the shell and echo paths could
        /// not have caught it.
        let boundSecrets = ResizeArray<SecretBinding * int>()

        let engineNotes = ResizeArray<string>()
        let durableIds = ResizeArray<string>()

        let mutable status = BuildStatus.Success
        let statusLock = obj ()

        { Emit =
            fun line ->
                lock outputLock (fun () ->
                    let safe =
                        if boundSecrets.Count = 0 then
                            line
                        else
                            Secrets.mask (boundSecrets |> Seq.map fst |> List.ofSeq) line

                    output.Add safe)
          BindSecrets =
            fun bindings ->
                lock outputLock (fun () ->
                    for b in bindings do
                        boundSecrets.Add(b, output.Count))
          Output = fun () -> lock outputLock (fun () -> List.ofSeq output)
          OutputWithActiveSecrets =
            fun () ->
                lock outputLock (fun () ->
                    output
                    |> Seq.mapi (fun i l ->
                        l,
                        (boundSecrets
                         |> Seq.filter (fun (_, from) -> from <= i)
                         |> Seq.map fst
                         |> List.ofSeq))
                    |> List.ofSeq)
          Bump = fun s -> lock statusLock (fun () -> status <- BuildStatus.worstOf status s)
          Status = fun () -> lock statusLock (fun () -> status)
          RunClock = Diagnostics.Stopwatch.StartNew()
          MkDeadline =
            fun absMs ->
                let token = System.Threading.Interlocked.Increment &nextDeadlineToken.contents
                { AtMs = absMs; Token = token }
          RecordFired =
            fun deadline ->
                deadline
                |> Option.iter (fun d -> lock outputLock (fun () -> firedDeadlines.Add d.Token |> ignore))
          DeadlineDidFire =
            fun declared ->
                match declared with
                | Some d -> lock outputLock (fun () -> firedDeadlines.Contains d.Token)
                | None -> false
          NoteEngine = fun n -> lock outputLock (fun () -> engineNotes.Add n)
          EngineNotes = fun () -> lock outputLock (fun () -> List.ofSeq engineNotes)
          AddDurableId = fun i -> lock outputLock (fun () -> durableIds.Add i)
          DurableIds = fun () -> lock outputLock (fun () -> List.ofSeq durableIds)
          ScriptBinding = GString.ScriptBinding() }
