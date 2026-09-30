namespace Fogell.Ir

type Position =
    { Line: int64
      Column: int64 }

    static member zero = { Line = 1L; Column = 1L }
    override this.ToString() = $"{this.Line}:{this.Column}"
