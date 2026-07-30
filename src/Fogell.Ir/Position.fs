namespace Fogell.Ir

/// Byte-accurate source position. Every rejection carries one: the charter
/// requires a named code *and* a position, because "your Jenkinsfile is
/// unsupported" without a line number is not a migration report.
type Position =
    { Line: int64
      Column: int64 }

    static member zero = { Line = 1L; Column = 1L }
    override this.ToString() = $"{this.Line}:{this.Column}"
