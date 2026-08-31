namespace Fogell.Execution

open System.Runtime.InteropServices

/// Fogell.Execution's DllImport surface (ADR 0006). Every entry point is
/// documented, and nothing here allocates or returns a pointer. Besides signal
/// and process-group primitives, the containment anchor uses Linux subreaper
/// ownership plus nonblocking waitpid so a zombie cannot keep a joinable group
/// alive after useful execution is extinct.
module internal Native =

    [<RequireQualifiedAccess>]
    type ProcessGroupQuery =
        | Found of int
        | Absent
        | Uncertain

    [<RequireQualifiedAccess>]
    type ProcessGroupPresence =
        | Present
        | Absent
        | Uncertain

    [<RequireQualifiedAccess>]
    type ChildReapResult =
        | Reaped
        | Running
        | NotChild
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

    [<Literal>]
    let private ECHILD = 10

    [<Literal>]
    let private WNOHANG = 1

    [<Literal>]
    let private PR_SET_CHILD_SUBREAPER = 36

    /// `kill(2)`. A negative pid targets the whole process group, which is how
    /// a step's descendants are signalled together rather than orphaned.
    [<DllImport("libc", SetLastError = true)>]
    extern int private kill(int pid, int signum)

    /// `getpgid(2)`. Used only to confirm a child really did lead a new group,
    /// so the tests assert the mechanism rather than trusting it.
    [<DllImport("libc", SetLastError = true)>]
    extern int private getpgid(int pid)

    /// `prctl(PR_SET_CHILD_SUBREAPER)`. Registered anchors orphaned by their
    /// session leader are reparented to Run.Host instead of an arbitrary PID 1.
    [<DllImport("libc", SetLastError = true)>]
    extern int private prctl(int option, nativeint arg2, nativeint arg3, nativeint arg4, nativeint arg5)

    /// `waitpid(2)` with a null status pointer. Callers pass only a PID observed
    /// in the registered group currently being reconciled; they never use -1 or
    /// harvest an unrelated concurrently running step's child.
    [<DllImport("libc", SetLastError = true)>]
    extern int private waitpid(int pid, nativeint status, int options)

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

    let probeProcessGroup (pgid: int) =
        if pgid <= 1 then
            ProcessGroupPresence.Uncertain
        else
            let result = kill (-pgid, 0)

            if result = 0 then
                ProcessGroupPresence.Present
            else
                match Marshal.GetLastWin32Error() with
                | ESRCH -> ProcessGroupPresence.Absent
                | 1 -> ProcessGroupPresence.Present
                | _ -> ProcessGroupPresence.Uncertain

    let enableChildSubreaper () =
        prctl(PR_SET_CHILD_SUBREAPER, nativeint 1, nativeint 0, nativeint 0, nativeint 0) = 0

    let tryReapChild pid =
        if pid <= 1 then
            ChildReapResult.Uncertain
        else
            let result = waitpid(pid, nativeint 0, WNOHANG)

            if result = pid then
                ChildReapResult.Reaped
            elif result = 0 then
                ChildReapResult.Running
            elif Marshal.GetLastWin32Error() = ECHILD then
                ChildReapResult.NotChild
            else
                ChildReapResult.Uncertain

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
