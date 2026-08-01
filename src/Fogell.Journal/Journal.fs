namespace Fogell.Journal

open System
open System.IO

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

/// ADR 0003. Append-only, per-attempt durable journal.
///
/// Append-only is the whole design: a record is never rewritten, so a torn write
/// can only ever truncate the tail. Resume therefore reads what it can and stops
/// at the first unparsable line rather than trusting a possibly-half-written
/// record — which is why [Record.decode] returns an option instead of throwing.
type Journal(path: string, policy: FsyncPolicy) =

    let mutable stream: FileStream option = None

    // FG-112: hooks fire from parallel BRANCH threads. Interleaved partial
    // writes tear a line, and Journal.read truncates at the first undecodable
    // line — resume would then forget durable steps and re-run them, the
    // at-least-once outcome this design exists to reject.
    let writeLock = obj ()

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

    member _.Path = path

    /// Append one record. Durability depends on the policy; the caller does not
    /// have to know which, only that a StepFinished is durable before the next
    /// step begins under EveryStep.
    member _.Append(record: Record) =
        lock writeLock (fun () ->
            let s = ensure ()
            let bytes = Text.Encoding.UTF8.GetBytes(Record.encode record + "\n")
            s.Write(bytes, 0, bytes.Length)
            s.Flush()

            match policy, record with
            | EveryStep, _ -> s.Flush true
            | EveryStage, StageCommitted _ -> s.Flush true
            | EveryStage, BuildFinished _ -> s.Flush true
            | EveryStage, _ -> ()
            | Never, _ -> ())

    /// Force whatever is buffered. Called before a controller hands an attempt
    /// away, so nothing in flight depends on a later flush.
    member _.Sync() =
        lock writeLock (fun () ->
            match stream with
            | Some s -> s.Flush true
            | None -> ())

    member _.Close() =
        lock writeLock (fun () ->
            match stream with
            | Some s ->
                s.Flush true
                s.Dispose()
                stream <- None
            | None -> ())

    interface IDisposable with
        member this.Dispose() = this.Close()

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

    /// Truncate a torn final line so every remaining line decodes — call
    /// BEFORE any read whose result gates a resume decision.
    let repairTail = JournalRepair.repairTail
