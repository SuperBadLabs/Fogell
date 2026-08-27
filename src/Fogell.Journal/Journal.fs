namespace Fogell.Journal

open System
open System.IO
open Fogell.Domain

module JournalRepair =

    /// FG-112: a kill mid-write leaves a torn final line. `read` stops at the
    /// first undecodable line, so newline-terminating the fragment (the first
    /// version of this repair) would hide every LATER record behind it.
    /// TRUNCATE instead: the torn record was never durable, and dropping it
    /// restores the invariant that every line decodes. Idempotent; safe on a
    /// missing or empty file.
    let repairTail (path: string) =
        if File.Exists path then
            use repair = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)

            if repair.Length > 0L then
                repair.Seek(-1L, SeekOrigin.End) |> ignore

                if repair.ReadByte() <> int '\n' then
                    // walk back to the last newline; everything after it is the fragment
                    let mutable pos = repair.Length - 1L

                    while pos > 0L do
                        repair.Seek(pos - 1L, SeekOrigin.Begin) |> ignore
                        if repair.ReadByte() = int '\n' then pos <- -pos else pos <- pos - 1L

                    repair.SetLength(max 0L (abs pos))
                    repair.Flush true


/// When the journal is forced to disk.
type FsyncPolicy =
    /// One fsync per step. Safest, and the measured floor is ~7.7 ms per step on
    /// a SATA SSD — already ~7x better than Jenkins at equal guarantees, because
    /// Jenkins spends ~6.9 fsyncs per step.
    | EveryStep
    /// One fsync per stage boundary. Measured target ~0.40 ms/step amortised,
    /// ~134x Jenkins. A crash loses at most the current stage's step records,
    /// which resume treats as "may have started".
    | EveryStage
    /// No fsync. For tests only; never a production setting.
    | Never

/// FG-207. Test-only observation of a force that the journal itself performed.
/// The observer is deliberately a notification, not an injected flush function:
/// production and tests both execute the same private `FileStream.Flush(true)`.
type internal ForceOrigin =
    | AppendPolicy
    | ExplicitSync
    | Closing

type internal ForceObservation =
    { Origin: ForceOrigin
      RecordCount: int
      Length: int64 }

/// ADR 0003. Append-only, per-attempt durable journal.
///
/// Append-only is the whole design: a record is never rewritten, so a torn write
/// can only ever truncate the tail. Resume therefore reads what it can and stops
/// at the first unparsable line rather than trusting a possibly-half-written
/// record — which is why [Record.decode] returns an option instead of throwing.
type Journal internal (path: string, policy: FsyncPolicy, forceObserver: (ForceObservation -> unit) option) =

    let mutable stream: FileStream option = None

    // FG-112: hooks fire from parallel BRANCH threads. Interleaved partial
    // writes tear a line, and Journal.read truncates at the first undecodable
    // line — resume would then forget durable steps and re-run them, the
    // at-least-once outcome this design exists to reject.
    let writeLock = obj ()

    // The real durability primitive. An observer can count a COMPLETED force,
    // but cannot replace it, suppress it, or choose a weaker flush mode.
    let force origin recordCount (s: FileStream) =
        s.Flush true

        forceObserver
        |> Option.iter (fun observe ->
            observe
                { Origin = origin
                  RecordCount = recordCount
                  Length = s.Length })

    let ensure () =
        match stream with
        | Some s -> s
        | None ->
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

            // FG-112: torn-tail truncation — see Journal.repairTail, which must
            // ALSO run before any read that feeds a resume decision (the
            // reconciliation-refusal path exits before this open runs, and an
            // operator's appended fix would land invisibly behind the fragment).
            JournalRepair.repairTail path

            let s = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
            stream <- Some s
            s

    let appendGroup (records: Record list) =
        if not (List.isEmpty records) then
            lock writeLock (fun () ->
                let s = ensure ()

                // One lock owns every byte in the completion group. Encoding one
                // contiguous buffer also makes Finish/Reason adjacency independent
                // of FileStream's per-call buffering decisions.
                let bytes =
                    records
                    |> List.map (fun record -> Record.encode record + "\n")
                    |> String.concat ""
                    |> Text.Encoding.UTF8.GetBytes

                s.Write(bytes, 0, bytes.Length)
                s.Flush()

                match policy with
                | EveryStep -> force AppendPolicy records.Length s
                | EveryStage
                    when records
                         |> List.exists (function
                             | StageCommitted _
                             | BuildFinished _ -> true
                             | _ -> false) ->
                    force AppendPolicy records.Length s
                | EveryStage
                | Never -> ())

    member _.Path = path

    /// Append one record. Durability depends on the policy; the caller does not
    /// have to know which, only that a StepFinished is durable before the next
    /// step begins under EveryStep.
    member _.Append(record: Record) = appendGroup [ record ]

    /// FG-207. A finish and its optional explanation are one durability unit.
    /// Their wire format stays two records, Finish first, so every historical
    /// reader remains valid; EveryStep pays one force only after both bytes land.
    ///
    /// A reason on Success/Unstable would describe a green disposition and is
    /// rejected. Failure/Aborted WITHOUT a reason remains valid: FG-114 captures
    /// only named diagnostic sites, and historical journals/resume explicitly
    /// tolerate a durable disposition whose explanation was not captured.
    member _.AppendStepFinished(stage: string, stepIndex: int, status: BuildStatus, reason: string option) =
        let records =
            match status, reason with
            | (BuildStatus.Success | BuildStatus.Unstable), Some _ ->
                invalidArg (nameof reason) "a successful or unstable step cannot carry a failure reason"
            | BuildStatus.NotBuilt, _ ->
                invalidArg (nameof status) "NotBuilt is not a completed step status"
            | _, Some explanation ->
                [ StepFinished(stage, stepIndex, status)
                  StepReason(stage, stepIndex, explanation) ]
            | _, None -> [ StepFinished(stage, stepIndex, status) ]

        appendGroup records

    /// Force whatever is buffered. Called before a controller hands an attempt
    /// away, so nothing in flight depends on a later flush.
    member _.Sync() =
        lock writeLock (fun () ->
            match stream with
            | Some s -> force ExplicitSync 0 s
            | None -> ())

    member _.Close() =
        lock writeLock (fun () ->
            match stream with
            | Some s ->
                force Closing 0 s
                s.Dispose()
                stream <- None
            | None -> ())

    interface IDisposable with
        member this.Dispose() = this.Close()

    /// Preserve the original public construction surface. The observer is an
    /// internal test seam, not a new production dependency.
    new (path: string, policy: FsyncPolicy) = new Journal(path, policy, None)

module Journal =

    /// Read a journal, stopping at the first line that does not decode.
    ///
    /// A crash can leave a partially written final line. Treating that as a hard
    /// error would make an otherwise-recoverable attempt unrecoverable; treating
    /// it as valid would invent a transition that never happened. Stopping is the
    /// only honest option, and it is why records are written one line at a time.
    let read (path: string) : Record list =
        if not (File.Exists path) then
            []
        else
            File.ReadAllLines path
            |> Array.toList
            |> List.map Record.decode
            |> List.takeWhile Option.isSome
            |> List.map Option.get

    let openAt path policy = new Journal(path, policy)

    /// Deterministic force observation for this assembly's tests. The callback
    /// runs only after the same real Flush(true) production uses has returned.
    let internal openAtObserved path policy observer =
        new Journal(path, policy, Some observer)

    /// Truncate a torn final line so every remaining line decodes — call
    /// BEFORE any read whose result gates a resume decision.
    let repairTail = JournalRepair.repairTail
