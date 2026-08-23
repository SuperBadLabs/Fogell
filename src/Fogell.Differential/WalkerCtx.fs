namespace Fogell.Differential

open System
open Fogell.Domain
open Fogell.Execution

/// FG-105. The walker's run-scoped mutable state — one value constructed per
/// build, passed explicitly where the 2,000-line closure used to capture it.
///
/// Contract, stated once:
///  * this record is the only RUN-scoped mutable state — a unit that wants new
///    run-wide state is asking for a new field, and the review sees the
///    request. (Branch-scoped signals live in BranchCtx; step-local mutables
///    in the orchestration bodies are their own scope's business.)
///  * every operation is safe to call from parallel branches. TWO internal
///    locks: outputLock orders output, secret registration, the fired-set,
///    engine notes and durable ids against each other — a line can never be
///    appended while the masker is unaware of a secret bound before it;
///    statusLock guards Bump/Status alone, so a Status() read is NOT ordered
///    with respect to output. Nothing may rely on such an ordering;
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
      /// FG-053. Turn on `options { timestamps() }` for the rest of the build.
      ///
      /// A SETTER rather than a `create` parameter because the pipeline's
      /// options are read after the context exists. It prefixes SUBSEQUENT
      /// `Emit` calls and nothing already written; WHERE it is called is the
      /// caller's decision and is placed at a MEASURED point — after SCM
      /// provenance and the default checkout, both of which Jenkins leaves
      /// unstamped because it cannot activate a Declarative option before it has
      /// fetched and parsed the file (receipt `options-timestamps-scm`, PARTIAL
      /// 1/21 on both engines).
      ///
      /// This said "from the moment the build starts", which is wrong and is the
      /// kind of wrong that gets acted on: a maintainer trusting it would move
      /// activation earlier and stamp the provenance line, which fails the SCM
      /// case. Idempotent; calling it twice does not double-prefix.
      EnableTimestamps: unit -> unit
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
      /// FG-220. One wall-clock origin captured when the build walk begins.
      /// JUnit `skipOldReports` compares every report against this origin, never
      /// against the later step-invocation time.
      BuildStartTimeInMillis: int64
      /// A resumed persisted attempt cannot recapture Jenkins' original build
      /// timestamp. FG-220 uses this to refuse skipOldReports rather than apply
      /// a silently newer cutoff.
      IsRestartedRun: bool
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
      ScriptBinding: GString.ScriptBinding
      /// FG-046b. Mask a string against every secret bound so far — the SAME
      /// run-scoped set `Emit` uses, for the same reason: a value does not become
      /// safe when its `withCredentials` block closes.
      ///
      /// It is exposed because build output is not the only place a secret can
      /// escape to. An `input` prompt interpolating a bound value is masked on
      /// the console and was then written VERBATIM to the controller-side
      /// `.pending` file — a file on disk, in an inbox that may be shared across
      /// builds, and one the output leak-guard never inspects because it is not
      /// output. Anything leaving the engine with author-supplied text in it has
      /// to come through here.
      MaskSecrets: string -> string
      /// FG-046b. The next occurrence ordinal for an `input` under a given
      /// durability key, counting from 1. The key alone is the TOP-LEVEL step,
      /// so `timeout { input 'deploy prod?'; input 'and the database?' }` gives
      /// both prompts the same key — and one human's answer, cached, then
      /// silently answered the second gate nobody reviewed. The ordinal
      /// separates them.
      ///
      /// Derived rather than stored, and that is what makes it survive: a
      /// resumed attempt re-runs the wrapper body from the start and counts the
      /// same prompts in the same order, so occurrence N names the same gate it
      /// named before the crash. Sequential by construction — parallel branches
      /// run nested STAGES, which carry their own keys.
      ///
      /// STATED LIMIT, and its bound. Derivation-by-re-execution assumes the
      /// path to a prompt is the same on both attempts. A wrapper body that
      /// reaches a DIFFERENT set of prompts on a resumed run — the shape to
      /// picture is a conditional on `isRestartedRun()` that adds a prompt ahead
      /// of an existing one — would renumber them, and occurrence 1 would then
      /// name a gate the recorded answer was never given to.
      ///
      /// That is unreachable as this engine stands, and the reason is worth
      /// stating rather than trusting: a recorded answer is only ever consulted
      /// by a RE-RUN, and the only step a resumed attempt re-runs to consult one
      /// is the reconciliation exemption in Resume.inputAnswered — which
      /// requires the journaled step name to be `input`, i.e. a BARE top-level
      /// prompt, which has exactly one occurrence. A WRAPPED prompt's step is
      /// interrupted, so the resume refuses it (or an operator reconciles it and
      /// the whole wrapper is skipped); either way its recorded answers are
      /// never re-matched by a fresh count. Widening that exemption to wrappers
      /// makes this limit live, and must not be done without replacing the
      /// derived ordinal with a durable one.
      NextInputOccurrence: string * int -> int }

module WalkerCtx =

    /// Build the run-scoped state. Everything mutable lives inside this call's
    /// closures; the returned record is the only handle.
    let create (buildStartTimeInMillis: int64) (isRestartedRun: bool) : WalkerCtx =
        let output = System.Collections.Generic.List<string>()
        // Parallel branches append from several threads at once; this one lock
        // also orders output against secret registration and the fired-set.
        let outputLock = obj ()

        let firedDeadlines = System.Collections.Generic.HashSet<int>()

        // FG-046b: how many `input` prompts have run under each durability key
        let inputOccurrences = System.Collections.Generic.Dictionary<string * int, int>()
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

        // FG-053. MEASURED on Jenkins 2.568.1: `options { timestamps() }` puts
        // an ISO-8601 instant in brackets ahead of the lines the BUILD prints —
        // measured 2/21 raw lines on a two-step build, NOT the [Pipeline]
        // annotations, banners or Started/Finished rows —
        // `[2026-08-03T03:54:07.729Z] + echo first`. Millisecond precision, UTC,
        // trailing space. Receipt: `options-timestamps`.
        let mutable timestamps = false

        { Emit =
            fun line ->
                lock outputLock (fun () ->
                    let safe =
                        if boundSecrets.Count = 0 then
                            line
                        else
                            Secrets.mask (boundSecrets |> Seq.map fst |> List.ofSeq) line

                    // AFTER masking, deliberately. The prefix carries no secret,
                    // and masking a line whose start has already moved would let
                    // an offset-based masker act on the wrong span.
                    let stamped =
                        if timestamps then
                            // INVARIANT CULTURE. `ToString(format)` uses the
                            // CURRENT culture, whose time separator is not always
                            // `:` — a process running under one of those would
                            // emit `T03.54.07.729Z`, which `Trace.timestampPrefix`
                            // does not match, so Fogell would neither strip nor
                            // count its own prefix. The engine's output would
                            // depend on the operator's locale.
                            let now =
                                System.DateTime.UtcNow.ToString(
                                    "yyyy-MM-ddTHH:mm:ss.fffZ",
                                    System.Globalization.CultureInfo.InvariantCulture)
                            $"[{now}] {safe}"
                        else
                            safe

                    output.Add stamped)
          EnableTimestamps = fun () -> lock outputLock (fun () -> timestamps <- true)
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
          BuildStartTimeInMillis = buildStartTimeInMillis
          IsRestartedRun = isRestartedRun
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
          ScriptBinding = GString.ScriptBinding()
          MaskSecrets =
            fun line ->
                lock outputLock (fun () ->
                    if boundSecrets.Count = 0 then
                        line
                    else
                        Secrets.mask (boundSecrets |> Seq.map fst |> List.ofSeq) line)
          NextInputOccurrence =
            // its own lock: this orders nothing against output or status, and
            // borrowing outputLock would make an approval wait behind a
            // parallel branch's logging for no reason
            fun key ->
                lock inputOccurrences (fun () ->
                    let next =
                        match inputOccurrences.TryGetValue key with
                        | true, n -> n + 1
                        | _ -> 1

                    inputOccurrences[key] <- next
                    next) }
