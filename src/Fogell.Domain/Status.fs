namespace Fogell.Domain

type BuildStatus =
    | NotBuilt
    | Success
    | Unstable
    | Failure
    | Aborted

module BuildStatus =

    let severity =
        function
        | NotBuilt -> 0
        | Success -> 1
        | Unstable -> 2
        | Failure -> 3
        | Aborted -> 4

    /// Associative, commutative, with [NotBuilt] as identity — a commutative
    /// monoid, which is what lets stage results be aggregated in any order and
    /// in parallel without changing the verdict.
    let worstOf (a: BuildStatus) (b: BuildStatus) : BuildStatus =
        if severity a >= severity b then a else b

    let ofMany (statuses: BuildStatus seq) : BuildStatus =
        Seq.fold worstOf NotBuilt statuses

    let isTerminal =
        function
        | Success
        | Unstable
        | Failure
        | Aborted -> true
        | NotBuilt -> false

    let toWireString =
        function
        | NotBuilt -> "not_built"
        | Success -> "success"
        | Unstable -> "unstable"
        | Failure -> "failure"
        | Aborted -> "aborted"

    let ofWireString =
        function
        | "not_built" -> Some NotBuilt
        | "success" -> Some Success
        | "unstable" -> Some Unstable
        | "failure" -> Some Failure
        | "aborted" -> Some Aborted
        | _ -> None
