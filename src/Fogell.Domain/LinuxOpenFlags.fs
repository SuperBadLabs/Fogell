namespace Fogell.Domain

open System.Runtime.InteropServices

/// FG-238. Linux `open(2)` flag bits are per-architecture kernel ABI, not one
/// number. `include/uapi/asm-generic/fcntl.h` defines O_DIRECTORY = 0o200000 and
/// O_NOFOLLOW = 0o400000, and x86, x86-64, riscv, s390x and loongarch use those;
/// arm, arm64 and powerpc override them in their own `asm/fcntl.h` to 0o40000
/// and 0o100000 (arm64 kept arm32's numbering), and on those machines the
/// generic values mean O_DIRECT and O_LARGEFILE instead. Opening a directory
/// with O_DIRECT is EINVAL, so a process that hardcodes the generic bits fails
/// every descriptor-policy open on arm64 and, worse, never asks the kernel for
/// no-follow at all. The other bits Fogell uses (O_NONBLOCK, O_CREAT, O_TRUNC,
/// O_CLOEXEC) are the same on every architecture .NET runs on.
[<RequireQualifiedAccess>]
module LinuxOpenFlags =

    type Table = { Directory: int; NoFollow: int }

    /// `include/uapi/asm-generic/fcntl.h`.
    let asmGeneric = { Directory = 0x10000; NoFollow = 0x20000 }

    /// `arch/arm64/include/uapi/asm/fcntl.h`; arm and powerpc carry the same values.
    let armLineage = { Directory = 0x4000; NoFollow = 0x8000 }

    /// The table for one process architecture, or the reason none is known. An
    /// untabulated architecture is a refusal, never a guess: a wrong bit pattern
    /// is silent right up to the moment it is a traversal.
    let forArchitecture (architecture: Architecture) : Result<Table, string> =
        match architecture with
        | Architecture.X86
        | Architecture.X64
        | Architecture.RiscV64
        | Architecture.S390x
        | Architecture.LoongArch64 -> Ok asmGeneric
        | Architecture.Arm
        | Architecture.Armv6
        | Architecture.Arm64
        | Architecture.Ppc64le -> Ok armLineage
        | other -> Error $"open(2) flag values are not tabulated for process architecture {other}"

    /// The table for the running process, resolved once.
    let current: Result<Table, string> =
        forArchitecture RuntimeInformation.ProcessArchitecture
