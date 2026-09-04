namespace Fogell.Differential

open System
open Fogell.Domain
open Fogell.Execution

/// Progressive output is controller infrastructure, not build semantics.  Keep
/// its failure typed across the walker/Run.Host boundary so the host can leave
/// the journal non-terminal and force reconciliation.
type OutputPublicationException(message: string, inner: exn) =
    inherit Exception(message, inner)

type private PublicationStream() =
    member val Completed = false with get, set

type private PendingPublication =
    { Order: int64
      OutputIndex: int
      Prefix: string
      Value: RedactedText
      /// False means admission already found an unregistered transformation.
      /// Keep its terminal evidence, and never let an EOF reframe make it
      /// eligible for external publication.
      Publishable: bool
      /// Stable identity for one raw stdout or stderr stream. Lines sharing
      /// this identity may be reassembled for a late-binding separator-aware
      /// recheck even when unrelated global output is interleaved.
      RedactedStream: PublicationStream option }

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
      /// Append shell output already processed by the run-scoped raw matcher.
      /// It still receives timestamps, leak screening, ordering, and external
      /// publication, while preserving exact matcher-token provenance.
      EmitRedacted: RedactedText -> unit
      /// Atomically admit ordinary output while letting the external publisher
      /// drain independently. ProcessGroup uses this inside its masking lock.
      Admit: string -> unit
      /// Mint one provenance-bearing admission lifecycle per raw stdout or
      /// stderr stream. EOF releases any suffix retained because a credential
      /// was registered after that stream's earlier line was admitted.
      CreateRedactedAdmission: unit -> RedactedAdmission
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
      /// Locked snapshot of every credential registered before this call.
      /// Shell raw-stream redaction consumes this run-scoped inventory; a
      /// credential does not become safe merely because its lexical binding
      /// ended or belongs to another branch.
      BoundSecrets: unit -> SecretBinding list
      /// The registration/publication lock shared with raw stream masking so a
      /// credential cannot land between inventory sampling and queue admission.
      MaskingSecretsLock: obj
      /// Locked snapshot of the raw output lines, in emission order.
      Output: unit -> string list
      /// Wait for the ordered external publisher and surface its first failure.
      /// Persisted hosts call this before constructing terminal truth.
      FlushOutput: unit -> unit
      /// Locked snapshot pairing each line with the secrets that were already
      /// bound when it was emitted, its shell-provenance-bearing value and its
      /// engine prefix — the leak scan's exact input.
      OutputWithActiveSecrets: unit -> (string * SecretBinding list * bool * string * RedactedText) list
      /// Transformed evidence discovered while rechecking output which was
      /// queued before a later credential binding. Such output is never
      /// published, but must still make terminal trace construction fail closed.
      PublicationLeaks: unit -> Leak list
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
    let create
        (buildStartTimeInMillis: int64)
        (isRestartedRun: bool)
        (onOutput: (string -> unit) option)
        : WalkerCtx =
        let output = System.Collections.Generic.List<string>()
        // Parallel branches append from several threads at once; this one lock
        // also orders output against secret registration and the fired-set.
        let outputLock = obj ()

        // Emit assigns publication order while holding outputLock, then one
        // drainer invokes the external callback without that lock.  A callback
        // may therefore block or re-enter Emit without freezing masking, trace
        // reads, or parallel writers. Monitor re-entrancy lets a nested Emit
        // enqueue and return; the outer drain publishes it next.
        let publicationLock = obj ()
        let publications = System.Collections.Generic.Queue<PendingPublication>()
        let deferredPublications = ResizeArray<PendingPublication>()
        let barrierStreams = System.Collections.Generic.HashSet<PublicationStream>(HashIdentity.Reference)
        let openStreams = System.Collections.Generic.HashSet<PublicationStream>(HashIdentity.Reference)
        let streamHistory =
            System.Collections.Generic.Dictionary<PublicationStream, ResizeArray<PendingPublication>>(HashIdentity.Reference)
        let committedPublicationOrders = System.Collections.Generic.HashSet<int64>()
        let mutable nextPublicationOrder = 0L
        let mutable publicationActive = false
        let mutable publicationFailure: OutputPublicationException option = None

        let drainPublications publish =
            let mutable draining = true

            while draining do
                let next =
                    lock publicationLock (fun () ->
                        if publications.Count = 0 then
                            publicationActive <- false
                            System.Threading.Monitor.PulseAll publicationLock
                            None
                        else
                            let item = publications.Dequeue()
                            // Once dequeued, the external call is irrevocably in
                            // flight. A later credential may use this item as
                            // left-context, but must never publish it again.
                            committedPublicationOrders.Add item.Order |> ignore
                            Some item)

                match next with
                | None -> draining <- false
                | Some item ->
                    try
                        // External code runs with neither WalkerCtx lock held.
                        publish (item.Prefix + item.Value.Text)
                    with ex ->
                        let failure =
                            match ex with
                            | :? OutputPublicationException as typed -> typed
                            | _ -> OutputPublicationException("progressive output publication failed", ex)

                        lock publicationLock (fun () ->
                            publicationFailure <- Some failure
                            publications.Clear()
                            deferredPublications.Clear()
                            barrierStreams.Clear()
                            publicationActive <- false
                            System.Threading.Monitor.PulseAll publicationLock)
                        raise failure

        let flushPublications () =
            match onOutput with
            | None -> ()
            | Some publish ->
                let mutable complete = false

                while not complete do
                    let shouldDrain =
                        lock publicationLock (fun () ->
                            match publicationFailure with
                            | Some failure -> raise failure
                            | None when not publicationActive && publications.Count > 0 ->
                                publicationActive <- true
                                true
                            | None when publicationActive ->
                                System.Threading.Monitor.Wait publicationLock |> ignore
                                false
                            | None when barrierStreams.Count > 0 ->
                                let failure =
                                    OutputPublicationException(
                                        "progressive output stream did not reach EOF",
                                        InvalidOperationException("redacted publication lifecycle remained incomplete"))

                                publicationFailure <- Some failure
                                deferredPublications.Clear()
                                barrierStreams.Clear()
                                raise failure
                            | None ->
                                complete <- true
                                false)

                    if shouldDrain then
                        drainPublications publish

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
        let publicationLeaks = ResizeArray<Leak>()
        let redactedOutputIndexes = System.Collections.Generic.HashSet<int>()
        let outputPrefixes = ResizeArray<string>()
        let outputValues = ResizeArray<RedactedText>()
        let suppressedOutputIndexes = System.Collections.Generic.HashSet<int>()

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

        let startDeferredDrain publish =
            System.Threading.ThreadPool.QueueUserWorkItem(fun _ ->
                try
                    drainPublications publish
                with :? OutputPublicationException ->
                    // The failure is sticky in publicationFailure and is
                    // surfaced by FlushOutput at the persisted boundary.
                    ())
            |> ignore

        let publicationItemLeaks secrets (item: PendingPublication) (value: RedactedText) =
            Secrets.detectUnregisteredLeaksRedacted secrets value
            @ Secrets.detectLeaks secrets item.Prefix
            @ Secrets.detectBoundaryLeaks secrets item.Prefix value

        let retainPublicationLeaks leaks =
            for leak in leaks do
                if not (publicationLeaks.Contains leak) then
                    publicationLeaks.Add leak

        let remaskIntoPublicationQueue secrets (pending: PendingPublication array) =
            let remasked = ResizeArray<PendingPublication>()
            let streamGroups =
                System.Collections.Generic.Dictionary<PublicationStream, ResizeArray<PendingPublication>>(HashIdentity.Reference)

            for item in pending do
                match item.RedactedStream with
                | None ->
                    let value = Secrets.maskAlreadyRedacted secrets item.Value
                    let leaks = publicationItemLeaks secrets item value
                    let rechecked =
                        { item with
                            Value = value
                            Publishable = item.Publishable && List.isEmpty leaks }

                    if rechecked.Publishable then
                        output[item.OutputIndex] <- item.Prefix + value.Text
                        outputValues[item.OutputIndex] <- value
                        redactedOutputIndexes.Add item.OutputIndex |> ignore
                        remasked.Add rechecked
                    else
                        retainPublicationLeaks leaks
                | Some stream ->
                    match streamGroups.TryGetValue stream with
                    | true, group -> group.Add item
                    | false, _ ->
                        let group = ResizeArray<PendingPublication>()
                        group.Add item
                        streamGroups.Add(stream, group)

            for group in streamGroups.Values do
                let stream = group[0].RedactedStream.Value
                let source =
                    match streamHistory.TryGetValue stream with
                    | true, history -> history.ToArray()
                    // A completed stream can still own output queued behind an
                    // earlier blocked callback. Its pending group is complete
                    // history for the only bytes which remain publishable.
                    | false, _ -> group.ToArray()
                // An already unsafe fragment is terminal refusal evidence.
                // Preserve it verbatim instead of allowing line collapse to
                // erase that evidence; the whole stream remains unpublished.
                if source |> Array.forall _.Publishable then
                    let reframed =
                        source
                        |> Array.map _.Value
                        |> Secrets.maskAlreadyRedactedLines secrets

                    let rechecked =
                        reframed
                        |> Array.map (fun (sourceIndex, value) ->
                            let item = source[sourceIndex]
                            let leaks =
                                if committedPublicationOrders.Contains item.Order then
                                    []
                                else
                                    publicationItemLeaks secrets item value
                            { item with
                                Value = value
                                Publishable = item.Publishable && List.isEmpty leaks },
                            leaks)

                    let streamLeaks =
                        rechecked
                        |> Array.collect (snd >> List.toArray)
                        |> Array.toList

                    if List.isEmpty streamLeaks then
                        for item in source do
                            suppressedOutputIndexes.Add item.OutputIndex |> ignore

                        for item, _ in rechecked do
                            output[item.OutputIndex] <- item.Prefix + item.Value.Text
                            outputValues[item.OutputIndex] <- item.Value
                            suppressedOutputIndexes.Remove item.OutputIndex |> ignore

                            remasked.Add item
                    else
                        retainPublicationLeaks streamLeaks

            if Option.isSome onOutput then
                remasked
                |> Seq.filter (fun item -> not (committedPublicationOrders.Contains item.Order))
                |> Seq.sortBy _.Order
                |> Seq.iter publications.Enqueue

        let remaskPendingPublications secrets =
            lock publicationLock (fun () ->
                // Binding is the linearization point: bytes committed before
                // it were not yet a credential, while every still-open stream
                // is held from here through true EOF. Its retained history
                // supplies left-context without replaying committed bytes.
                for stream in openStreams do
                    barrierStreams.Add stream |> ignore

                let queued = publications.ToArray()
                publications.Clear()

                if barrierStreams.Count > 0 then
                    deferredPublications.AddRange queued
                else
                    remaskIntoPublicationQueue secrets queued
            )

        let completePublicationStream (stream: PublicationStream) =
            let shouldDrain =
                lock outputLock (fun () ->
                    let secrets = boundSecrets |> Seq.map fst |> List.ofSeq

                    lock publicationLock (fun () ->
                        stream.Completed <- true
                        openStreams.Remove stream |> ignore
                        barrierStreams.Remove stream |> ignore

                        if barrierStreams.Count = 0 && deferredPublications.Count > 0 then
                            let pending = deferredPublications.ToArray()
                            deferredPublications.Clear()
                            remaskIntoPublicationQueue secrets pending

                        if barrierStreams.Count = 0 then
                            streamHistory
                            |> Seq.choose (fun pair -> if pair.Key.Completed then Some pair.Key else None)
                            |> Seq.toArray
                            |> Array.iter (fun completed -> streamHistory.Remove completed |> ignore)

                        match publicationFailure with
                        | Some _ -> false
                        | None when barrierStreams.Count = 0 && not publicationActive && publications.Count > 0 ->
                            publicationActive <- true
                            true
                        | None -> false))

            match shouldDrain, onOutput with
            | true, Some publish -> startDeferredDrain publish
            | _ -> ()

        let emitCore deferExternalDrain alreadyRedacted redactedStream safeAndLeaks =
            let shouldDrain =
                lock outputLock (fun () ->
                    let secrets = boundSecrets |> Seq.map fst |> List.ofSeq
                    let (safeValue: RedactedText), leaks = safeAndLeaks secrets
                    let safe = safeValue.Text

                    // AFTER masking, deliberately. Keep the engine-authored
                    // prefix separate so its literal screening cannot make an
                    // offset-based masker act on a line whose start has moved.
                    let prefix =
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
                            $"[{now}] "
                        else
                            ""

                    // A transformed secret can survive the ordinary masker,
                    // and an engine-authored timestamp can itself equal a
                    // registered literal. The terminal trace refuses both
                    // later, but the progressive callback happens NOW and must
                    // not publish either first.
                    let safeToPublish =
                        List.isEmpty secrets
                        || (List.isEmpty leaks
                            && List.isEmpty (Secrets.detectLeaks secrets prefix)
                            && List.isEmpty (Secrets.detectBoundaryLeaks secrets prefix safeValue))

                    let stamped = prefix + safe

                    let outputIndex = output.Count
                    output.Add stamped
                    outputPrefixes.Add prefix
                    outputValues.Add safeValue

                    if alreadyRedacted then
                        redactedOutputIndexes.Add outputIndex |> ignore
                    // Enqueue under the trace lock, after masking/leak screening,
                    // so the assigned monotonic order is exactly Output() order.
                    // The single callback consumer runs below, outside this lock.
                    if Option.isSome redactedStream || (safeToPublish && Option.isSome onOutput) then
                        lock publicationLock (fun () ->
                            match publicationFailure with
                            | Some _ when deferExternalDrain -> false
                            | Some failure -> raise failure
                            | None ->
                                let order = nextPublicationOrder
                                nextPublicationOrder <- nextPublicationOrder + 1L
                                let item =
                                    { Order = order
                                      OutputIndex = outputIndex
                                      Prefix = prefix
                                      Value = safeValue
                                      Publishable = safeToPublish
                                      RedactedStream = redactedStream }

                                redactedStream
                                |> Option.iter (fun stream -> streamHistory[stream].Add item)

                                let requiredForBarrier =
                                    redactedStream
                                    |> Option.exists barrierStreams.Contains

                                let eligibleForTransport = safeToPublish && Option.isSome onOutput

                                if barrierStreams.Count > 0 && (requiredForBarrier || eligibleForTransport) then
                                    deferredPublications.Add item
                                    false
                                elif eligibleForTransport then
                                    publications.Enqueue item

                                    if publicationActive then
                                        false
                                    else
                                        publicationActive <- true
                                        true
                                else
                                    false)
                    else
                        false)

            match shouldDrain, onOutput, deferExternalDrain with
            | true, Some publish, true -> startDeferredDrain publish
            | true, Some publish, false -> drainPublications publish
            | _ -> ()

        let emit line =
            emitCore false false None (fun secrets ->
                let safe =
                    if List.isEmpty secrets then RedactedText.Raw line else Secrets.maskRedacted secrets line

                safe, Secrets.detectLeaks secrets safe.Text)

        let emitRedacted (line: RedactedText) =
            emitCore false true None (fun secrets ->
                let safe =
                    if List.isEmpty secrets then line else Secrets.maskAlreadyRedacted secrets line

                safe, Secrets.detectUnregisteredLeaksRedacted secrets safe)

        let admit line =
            emitCore true false None (fun secrets ->
                let safe =
                    if List.isEmpty secrets then RedactedText.Raw line else Secrets.maskRedacted secrets line

                safe, Secrets.detectLeaks secrets safe.Text)

        { Emit = emit
          EmitRedacted = emitRedacted
          Admit = admit
          CreateRedactedAdmission =
            fun () ->
                let stream = PublicationStream()

                lock outputLock (fun () ->
                    lock publicationLock (fun () ->
                        openStreams.Add stream |> ignore
                        streamHistory.Add(stream, ResizeArray())))

                { Admit =
                    fun (line: RedactedText) ->
                        emitCore true true (Some stream) (fun secrets ->
                            let safe =
                                if List.isEmpty secrets then line else Secrets.maskAlreadyRedacted secrets line

                            safe, Secrets.detectUnregisteredLeaksRedacted secrets safe)
                  Complete = fun () -> completePublicationStream stream }
          EnableTimestamps = fun () -> lock outputLock (fun () -> timestamps <- true)
          BindSecrets =
            fun bindings ->
                lock outputLock (fun () ->
                    for b in bindings do
                        boundSecrets.Add(b, output.Count)

                    if not (List.isEmpty bindings) then
                        boundSecrets
                        |> Seq.map fst
                        |> List.ofSeq
                        |> remaskPendingPublications)
          BoundSecrets =
            fun () ->
                lock outputLock (fun () -> boundSecrets |> Seq.map fst |> List.ofSeq)
          MaskingSecretsLock = outputLock
          Output =
            fun () ->
                lock outputLock (fun () ->
                    output
                    |> Seq.mapi (fun index value -> index, value)
                    |> Seq.choose (fun (index, value) ->
                        if suppressedOutputIndexes.Contains index then None else Some value)
                    |> List.ofSeq)
          FlushOutput = flushPublications
          OutputWithActiveSecrets =
            fun () ->
                lock outputLock (fun () ->
                    output
                    |> Seq.mapi (fun i l ->
                        if suppressedOutputIndexes.Contains i then
                            None
                        else
                            Some(
                                l,
                                (boundSecrets
                                 |> Seq.filter (fun (_, from) -> from <= i)
                                 |> Seq.map fst
                                 |> List.ofSeq),
                                redactedOutputIndexes.Contains i,
                                outputPrefixes[i],
                                outputValues[i]))
                    |> Seq.choose id
                    |> List.ofSeq)
          PublicationLeaks = fun () -> lock outputLock (fun () -> publicationLeaks |> List.ofSeq)
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
