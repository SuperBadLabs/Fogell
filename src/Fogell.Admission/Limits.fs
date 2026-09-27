namespace Fogell.Admission

/// Admission bounds checked before any execution or durable admission.
type Limits =
    { MaxSourceBytes: int; MaxNodes: int; MaxDepth: int
      MaxScalarBytes: int; MaxCollectionItems: int }
    static member defaults =
        { MaxSourceBytes = 262144; MaxNodes = 16384; MaxDepth = 16
          MaxScalarBytes = 16384; MaxCollectionItems = 4096 }
