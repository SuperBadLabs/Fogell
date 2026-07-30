namespace Fogell.Journal

open System
open System.IO

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

    let ensure () =
        match stream with
        | Some s -> s
        | None ->
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let s = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
            stream <- Some s
            s

    member _.Path = path

    /// Append one record. Durability depends on the policy; the caller does not
    /// have to know which, only that a StepFinished is durable before the next
    /// step begins under EveryStep.
    member _.Append(record: Record) =
        let s = ensure ()
        let bytes = Text.Encoding.UTF8.GetBytes(Record.encode record + "\n")
        s.Write(bytes, 0, bytes.Length)
        s.Flush()

        match policy, record with
        | EveryStep, _ -> s.Flush true
        | EveryStage, StageCommitted _ -> s.Flush true
        | EveryStage, BuildFinished _ -> s.Flush true
        | EveryStage, _ -> ()
        | Never, _ -> ()

    /// Force whatever is buffered. Called before a controller hands an attempt
    /// away, so nothing in flight depends on a later flush.
    member _.Sync() =
        match stream with
        | Some s -> s.Flush true
        | None -> ()

    member _.Close() =
        match stream with
        | Some s ->
            s.Flush true
            s.Dispose()
            stream <- None
        | None -> ()

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
