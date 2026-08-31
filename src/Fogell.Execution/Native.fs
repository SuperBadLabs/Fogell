namespace Fogell.Execution

open System.Runtime.InteropServices

/// Fogell.Execution's DllImport surface (ADR 0006). Every entry point is
/// documented, and nothing here allocates or returns a pointer — these are
/// signal and process-group primitives only.
module internal Native =

    [<RequireQualifiedAccess>]
    type ProcessGroupQuery =
        | Found of int
        | Absent
        | Uncertain

    /// POSIX signals Fogell uses. Values are the Linux/glibc numbers.
    [<Literal>]
    let SIGTERM = 15

    [<Literal>]
    let SIGKILL = 9

    [<Literal>]
    let SIGCONT = 18

    [<Literal>]
    let ESRCH = 3 // no such process

    /// `kill(2)`. A negative pid targets the whole process group, which is how
    /// a step's descendants are signalled together rather than orphaned.
    [<DllImport("libc", SetLastError = true)>]
    extern int private kill(int pid, int signum)

    /// `getpgid(2)`. Used only to confirm a child really did lead a new group,
    /// so the tests assert the mechanism rather than trusting it.
    [<DllImport("libc", SetLastError = true)>]
    extern int private getpgid(int pid)

    /// Signal a single process. Returns false when it no longer exists.
    let signalProcess (pid: int) (signum: int) : bool =
        if pid <= 0 then
            false
        else
            kill (pid, signum) = 0

    /// This process's own group. Signalling it would kill the controller, so
    /// [signalGroup] refuses to do so.
    let ownProcessGroup () : int =
        match getpgid 0 with
        | -1 -> -1
        | g -> g

    /// Signal an entire process group. `pgid` is positive; the negation is
    /// applied here so callers cannot get the sign wrong.
    ///
    /// SAFETY GUARD: refuses to signal our own process group. Learned the hard
    /// way — `setsid --wait` forks, so the pid .NET reports is setsid's, whose
    /// group is the *parent's*. Reaping therefore sent SIGTERM to the controller
    /// itself. The pgid derivation is fixed, and this guard makes the whole
    /// class of mistake unable to recur.
    let signalGroup (pgid: int) (signum: int) : bool =
        if pgid <= 0 then
            false
        elif pgid = ownProcessGroup () then
            false
        else
            kill (-pgid, signum) = 0

    /// True when the process (or group leader) is still present.
    let processExists (pid: int) : bool = pid > 0 && kill (pid, 0) = 0

    let internal classifyProcessGroupQuery result error =
        if result >= 0 then
            ProcessGroupQuery.Found result
        elif error = ESRCH then
            ProcessGroupQuery.Absent
        else
            ProcessGroupQuery.Uncertain

    let queryProcessGroup (pid: int) =
        if pid <= 0 then
            ProcessGroupQuery.Absent
        else
            let result = getpgid pid
            classifyProcessGroupQuery result (Marshal.GetLastWin32Error())

    let processGroupOf (pid: int) : int option =
        match queryProcessGroup pid with
        | ProcessGroupQuery.Found pgid -> Some pgid
        | ProcessGroupQuery.Absent
        | ProcessGroupQuery.Uncertain -> None
