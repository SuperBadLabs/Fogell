namespace Fogell.Groovy.Interpreter

open Fogell.Groovy

/// Runtime values. Deliberately closed: there is no case that wraps an
/// arbitrary .NET object, because the moment one exists the sandbox is a
/// suggestion rather than a boundary (ADR 0002 / FG-072).
type Value =
    | VNull
    | VBool of bool
    | VInt of int64
    | VStr of string
    /// FG-015b. Groovy lists are reference objects. The ref is list identity:
    /// aliases, nested selections and method results share mutations, while a
    /// newly projected/collected list receives a fresh identity.
    | VList of Value list ref
    /// Groovy IntRange is list-like for reads, equality and iteration, but it is
    /// not an ArrayList and rejects replacement. Keep its immutable provenance
    /// distinct so FG-015b list writes can never silently mutate a range.
    | VRange of int64 list
    /// FG-193. A Groovy map is a REFERENCE object: aliases see each other's
    /// mutations. The ref is the identity, exactly as ref cells are for locals —
    /// measured: `def other = local; other.FOO = 'x'` printed alias:x on Jenkins
    /// and alias:null here while the write vanished into a dropped match arm.
    | VMap of Map<string, Value> ref
    /// FG-177 slice 5. A closed projection of Jenkins'
    /// `hudson.tasks.junit.TestResultSummary`. This is deliberately nominal rather
    /// than a map: map lookup, indexing, mutation and structural equality would add
    /// object surface Jenkins does not grant merely because three count properties
    /// are measured.
    | VJUnitSummary of JUnitSummary ref
    | VClosure of Closure * Env
    | VFunc of name: string * parameters: string list * body: Stmt list

and JUnitSummary =
    { TotalCount: int64
      FailCount: int64
      SkipCount: int64 }

and Env =
    /// FG-179. `Vars` maps a local name to a REF CELL, not to a value, and the indirection
    /// is the whole ticket.
    ///
    /// Groovy closures capture by REFERENCE. A closure that assigns to a name from its
    /// enclosing scope changes the enclosing scope's variable, and every later read there
    /// sees it. With `Map<string, Value>` that is not expressible: `VClosure` holds the
    /// `Env` it was created with, assignment rebuilt the map functionally, and the caller
    /// kept the OLD map — so the mutation existed only inside the closure and vanished when
    /// it returned.
    ///
    /// WHAT THE CELL CHANGES, precisely: `def x = …` still binds a NEW cell in a NEW map,
    /// so shadowing and lexical scope work exactly as before — a `def` inside a closure is
    /// invisible outside it. An ASSIGNMENT to a name already bound as a local now writes
    /// THROUGH the cell, so every `Env` sharing it observes the write. That is the
    /// difference between capturing a variable and copying its value.
    ///
    /// Both paths that reach a closure are receipted: `script-closure-mutates-enclosing`
    /// for a hosted wrapper body, `script-closure-mutates-ordinary` for a builtin's
    /// trailing block. THOSE RECEIPTS ARE THE STATEMENT OF CURRENT BEHAVIOUR, and this
    /// comment deliberately does not repeat it. An earlier version listed the findings that
    /// motivated the change, and when ONE of them turned out to be a different bug entirely
    /// (FG-187, a postfix index crossing a newline) the list contradicted the paragraph
    /// below it — a comment disagreeing with itself about a ticket it does not own. The
    /// finding history lives on the board, which is written to be edited.
    ///
    /// FG-195. `Funcs` maps a name to its CANDIDATES, because Groovy resolves a call by
    /// SIGNATURE and a name may carry several: `def pick()` beside `def pick(v)` is an
    /// ordinary overload pair, and folding them into one slot by name made `pick()` run
    /// the one-arg body — a call landing on something the language would not pick.
    /// Candidates stay in declaration order; resolution is by arity at the call.
    { Vars: Map<string, Value ref>
      Funcs: Map<string, (string list * Stmt list) list> }

type private ReferencePairComparer() =
    interface System.Collections.Generic.IEqualityComparer<obj * obj> with
        member _.Equals((leftA, leftB), (rightA, rightB)) =
            System.Object.ReferenceEquals(leftA, rightA)
            && System.Object.ReferenceEquals(leftB, rightB)

        member _.GetHashCode((left, right)) =
            let leftHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode left
            let rightHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode right
            (leftHash * 397) ^^^ rightHash

module Value =

    let rangeItems values = values |> List.map VInt

    type ReferenceCycleScan =
        { HasCycle: bool
          ReferencesVisited: int
          EdgesVisited: int }

    type private ReferenceCycleFrame =
        | InspectReference of Value
        | CompleteReference of obj

    /// Whether a value graph contains a reference cycle of any length. This is
    /// deliberately stricter than [tryToDisplay]: Groovy's collection renderer
    /// has direct-self markers, but Jenkins' step argument coercion/hash path
    /// still overflows for those same values before a hosted call is dispatched.
    /// Repeated aliases on sibling paths are not cycles, so [active] is the
    /// ancestry boundary. [completed] is separately memoized: once one acyclic
    /// reference and all of its descendants have been discharged, a sibling
    /// alias cannot disclose a new back edge and is skipped in O(1).
    let scanReferenceCycles value =
        let active = System.Collections.Generic.HashSet<obj>(HashIdentity.Reference)
        let completed = System.Collections.Generic.HashSet<obj>(HashIdentity.Reference)
        let pending = System.Collections.Generic.List<ReferenceCycleFrame>()
        let mutable cycle = false
        let mutable referencesVisited = 0
        let mutable edgesVisited = 0

        pending.Add(InspectReference value)

        let enter identity children =
            if completed.Contains identity then
                ()
            elif active.Contains identity then
                cycle <- true
            else
                active.Add identity |> ignore
                referencesVisited <- referencesVisited + 1
                edgesVisited <- edgesVisited + List.length children
                pending.Add(CompleteReference identity)

                // LIFO: the completion marker stays below every child, so
                // [active] is exactly the current DFS ancestry. A later alias
                // reaches [completed] in O(1) rather than expanding again.
                for child in List.rev children do
                    pending.Add(InspectReference child)

        while not cycle && pending.Count > 0 do
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt last

            match frame with
            | InspectReference current ->
                match current with
                | VList items -> enter (box items) items.Value
                | VMap entries -> enter (box entries) (entries.Value |> Map.toList |> List.map snd)
                | VNull
                | VBool _
                | VInt _
                | VStr _
                | VRange _
                | VJUnitSummary _
                | VClosure _
                | VFunc _ -> ()
            | CompleteReference identity ->
                active.Remove identity |> ignore
                completed.Add identity |> ignore

        { HasCycle = cycle
          ReferencesVisited = referencesVisited
          EdgesVisited = edgesVisited }

    let hasReferenceCycle value = (scanReferenceCycles value).HasCycle

    /// FG-191. What a comparison of two values CAME TO — a plain bool, or the
    /// discovery of a reference cycle that structural recursion would chase
    /// forever. MEASURED (receipt `script-cyclic-map-eq`): two self-referential maps compared with `==` was a
    /// STACK OVERFLOW that killed the process, not a fault — the worst outcome
    /// this engine can produce, because nothing can catch it and no receipt can
    /// see it. The caller decides what a cycle means in its context; this type
    /// is what stops the answer being decided by the runtime dying first.
    type Compared =
        | Answer of bool
        | CycleDetected
        | Unmodelled

    /// A total, host-safe answer for the subset of Groovy values which Fogell
    /// orders. `OrderingCycleDetected` means structural comparison revisited the
    /// same pair and Jenkins would overflow; `Unorderable` keeps closures and
    /// functions out of the host runtime's generic comparer.
    type Ordered =
        | Order of int
        | OrderingCycleDetected
        | Unorderable

    type Displayed =
        | Text of string
        | DisplayCycleDetected

    /// Direct self entries use Groovy's own `(this Collection)` / `(this Map)`
    /// markers. A longer reference cycle is different: Jenkins 2.568.1 chases
    /// list→map→list display into a catchable StackOverflowError. Report that
    /// boundary to the interpreter instead of recursing in the host process.
    let tryToDisplay v : Displayed =
        let mutable cycle = false

        let rec displayWith (seen: obj list) value =
            let seenRef candidate =
                seen |> List.exists (fun prior -> System.Object.ReferenceEquals(prior, candidate))

            match value with
            | VNull -> "null"
            | VBool b -> if b then "true" else "false"
            | VInt i -> string i
            | VStr s -> s
            | VList xs ->
                if seenRef xs then
                    cycle <- true
                    ""
                else
                    let inner = box xs :: seen

                    "["
                    + (xs.Value
                       |> List.map (function
                           | VList child when System.Object.ReferenceEquals(child, xs) -> "(this Collection)"
                           | item -> displayWith inner item)
                       |> String.concat ", ")
                    + "]"
            | VRange values ->
                "[" + (values |> List.map string |> String.concat ", ") + "]"
            | VMap m ->
                if seenRef m then
                    cycle <- true
                    ""
                else
                    let inner = box m :: seen

                    "["
                    + (m.Value
                       |> Map.toList
                       |> List.map (fun (k, item) ->
                           let shown =
                               match item with
                               | VMap child when System.Object.ReferenceEquals(child, m) -> "(this Map)"
                               | _ -> displayWith inner item

                           $"{k}:{shown}")
                       |> String.concat ", ")
                    + "]"
            | VJUnitSummary _ -> "<junit-test-result-summary>"
            | VClosure _ -> "<closure>"
            | VFunc(n, _, _) -> $"<function {n}>"

        let rendered = displayWith [] v
        if cycle then DisplayCycleDetected else Text rendered

    let toDisplay v =
        match tryToDisplay v with
        | Text rendered -> rendered
        | DisplayCycleDetected -> "<cyclic collection>"

    /// Finds the deliberately opaque JUnit value even when a script wraps it in
    /// a collection before passing it to a rendering or hosted-call boundary.
    let containsJUnitSummary value =
        match value with
        | VJUnitSummary _ -> true
        | VList _
        | VMap _ ->
            // Script-owned collections can be nested to the evaluation budget.
            // Keep that shape off the native call stack, and allocate the graph
            // walk only when the root can actually contain another Value.
            let seen = System.Collections.Generic.HashSet<obj>(HashIdentity.Reference)
            let pending = System.Collections.Generic.Stack<Value>()
            let mutable found = false
            pending.Push value

            while not found && pending.Count > 0 do
                match pending.Pop() with
                | VJUnitSummary _ -> found <- true
                | VList values when seen.Add(box values) ->
                    for item in values.Value do
                        pending.Push item
                | VMap values when seen.Add(box values) ->
                    for KeyValue(_, item) in values.Value do
                        pending.Push item
                | _ -> ()

            found
        | VNull
        | VBool _
        | VInt _
        | VStr _
        | VRange _
        | VClosure _
        | VFunc _ -> false

    /// FG-191. Equality that cannot kill the process, with Groovy's own rules:
    ///
    ///   - CLOSURES compare by IDENTITY — Groovy closures are reference-equal or
    ///     not equal. Identity here is the pair (AST node, captured Env record)
    ///     by reference: an aliased closure shares both; two evaluations of one
    ///     literal differ in the env record; two literals differ in the AST.
    ///     MEASURED before this existed (receipt `script-same-ast-closure-eq`): two SAME-AST closures from two calls
    ///     compared structurally walked their captured cells into the cycle and
    ///     the process died.
    ///   - MAPS compare pairwise through their refs; the same ref is equal
    ///     outright, and a DISTINCT pair met twice on one path is a cycle —
    ///     reported, not chased, because Groovy's own AbstractMap.equals chases
    ///     it into a JVM StackOverflowError there.
    ///   - lists and maps both compare through reference cells, with repeated
    ///     distinct pairs reported rather than chased.
    let tryEq (a: Value) (b: Value) : Compared =
        let mutable cycle = false
        let completed = System.Collections.Generic.HashSet<obj * obj>(ReferencePairComparer())

        let rec go (seen: (obj * obj) list) a b =
            match a, b with
            | VMap ma, VMap mb ->
                let pair = box ma, box mb

                if System.Object.ReferenceEquals(ma, mb) then
                    true
                elif completed.Contains pair then
                    true
                elif
                    seen
                    |> List.exists (fun (x, y) ->
                        System.Object.ReferenceEquals(x, ma) && System.Object.ReferenceEquals(y, mb))
                then
                    cycle <- true
                    false
                else
                    let inner = pair :: seen

                    let equal =
                        ma.Value.Count = mb.Value.Count
                        && ma.Value
                           |> Map.forall (fun k v ->
                               match Map.tryFind k mb.Value with
                               | Some w -> go inner v w
                               | None -> false)

                    if equal && not cycle then completed.Add pair |> ignore
                    equal
            | VList xa, VList xb ->
                let pair = box xa, box xb

                if System.Object.ReferenceEquals(xa, xb) then
                    true
                elif completed.Contains pair then
                    true
                elif
                    seen
                    |> List.exists (fun (x, y) ->
                        System.Object.ReferenceEquals(x, xa) && System.Object.ReferenceEquals(y, xb))
                then
                    cycle <- true
                    false
                else
                    let inner = pair :: seen
                    let equal = xa.Value.Length = xb.Value.Length && List.forall2 (go inner) xa.Value xb.Value
                    if equal && not cycle then completed.Add pair |> ignore
                    equal
            | VRange xa, VRange xb -> xa = xb
            | VRange xa, VList xb ->
                let left = rangeItems xa
                left.Length = xb.Value.Length && List.forall2 (go seen) left xb.Value
            | VList xa, VRange xb ->
                let right = rangeItems xb
                xa.Value.Length = right.Length && List.forall2 (go seen) xa.Value right
            | VClosure(c1, e1), VClosure(c2, e2) ->
                System.Object.ReferenceEquals(c1, c2) && System.Object.ReferenceEquals(e1, e2)
            | VClosure _, _
            | _, VClosure _
            | VMap _, _
            | _, VMap _
            | VList _, _
            | _, VList _
            | VRange _, _
            | _, VRange _ -> false
            | _ -> a = b

        if containsJUnitSummary a || containsJUnitSummary b then
            Unmodelled
        else
            let answer = go [] a b
            if cycle then CycleDetected else Answer answer

    /// Cycle-aware structural ordering for collection builtins. This preserves
    /// the old discriminated-union ordering for acyclic values while ensuring
    /// no runtime `compare` can follow a script-constructed reference cycle.
    ///
    /// Jenkins' top-level alias case (`[a, a].sort()`) returns normally, but two
    /// distinct wrapper lists which both contain the same cyclic `a` overflow.
    /// Therefore the identity shortcut belongs only at the comparator entry;
    /// recursive collection comparison must descend and detect the repeated pair.
    let tryCompare (a: Value) (b: Value) : Ordered =
        let mutable cycle = false
        let mutable unorderable = false
        let completed = System.Collections.Generic.HashSet<obj * obj>(ReferencePairComparer())

        let rank = function
            | VNull -> 0
            | VBool _ -> 1
            | VInt _ -> 2
            | VStr _ -> 3
            | VList _ -> 4
            | VRange _ -> 4
            | VMap _ -> 5
            | VJUnitSummary _ -> 6
            | VClosure _ -> 7
            | VFunc _ -> 8

        let seenPair (seen: (obj * obj) list) left right =
            seen
            |> List.exists (fun (priorLeft, priorRight) ->
                System.Object.ReferenceEquals(priorLeft, left)
                && System.Object.ReferenceEquals(priorRight, right))

        let rec compareValues seen left right =
            let leftRank = rank left
            let rightRank = rank right

            if leftRank <> rightRank then
                compare leftRank rightRank
            else
                match left, right with
                | VNull, VNull -> 0
                | VBool x, VBool y -> compare x y
                | VInt x, VInt y -> compare x y
                | VStr x, VStr y -> compare x y
                | VList xs, VList ys ->
                    let pair = box xs, box ys

                    if completed.Contains pair then
                        0
                    elif seenPair seen (box xs) (box ys) then
                        cycle <- true
                        0
                    else
                        let answer = compareLists (pair :: seen) xs.Value ys.Value
                        if answer = 0 && not cycle && not unorderable then completed.Add pair |> ignore
                        answer
                | VRange xs, VRange ys -> compareLists seen (rangeItems xs) (rangeItems ys)
                | VRange xs, VList ys -> compareLists seen (rangeItems xs) ys.Value
                | VList xs, VRange ys -> compareLists seen xs.Value (rangeItems ys)
                | VMap xs, VMap ys ->
                    let pair = box xs, box ys

                    if completed.Contains pair then
                        0
                    elif seenPair seen (box xs) (box ys) then
                        cycle <- true
                        0
                    else
                        let answer = compareEntries (pair :: seen) (Map.toList xs.Value) (Map.toList ys.Value)
                        if answer = 0 && not cycle && not unorderable then completed.Add pair |> ignore
                        answer
                | VJUnitSummary _, VJUnitSummary _
                | VClosure _, VClosure _
                | VFunc _, VFunc _ ->
                    unorderable <- true
                    0
                | _ -> 0

        and compareLists seen left right =
            match left, right with
            | [], [] -> 0
            | [], _ -> -1
            | _, [] -> 1
            | x :: xs, y :: ys ->
                let first = compareValues seen x y
                if first <> 0 || cycle || unorderable then first else compareLists seen xs ys

        and compareEntries seen left right =
            match left, right with
            | [], [] -> 0
            | [], _ -> -1
            | _, [] -> 1
            | (leftKey, leftValue) :: leftTail, (rightKey, rightValue) :: rightTail ->
                let keyOrder = compare leftKey rightKey

                if keyOrder <> 0 then
                    keyOrder
                else
                    let valueOrder = compareValues seen leftValue rightValue

                    if valueOrder <> 0 || cycle || unorderable then
                        valueOrder
                    else
                        compareEntries seen leftTail rightTail

        if containsJUnitSummary a || containsJUnitSummary b then
            Unorderable
        else
            let answer =
                match a, b with
                | VList xs, VList ys when System.Object.ReferenceEquals(xs, ys) -> 0
                | VMap xs, VMap ys when System.Object.ReferenceEquals(xs, ys) -> 0
                | _ -> compareValues [] a b

            if cycle then OrderingCycleDetected
            elif unorderable then Unorderable
            else Order answer

    /// Groovy truthiness: null, false, 0, "" and empty collections are falsy.
    let isTruthy =
        function
        | VNull -> false
        | VBool b -> b
        | VInt i -> i <> 0L
        | VStr s -> s <> ""
        | VList xs -> not (List.isEmpty xs.Value)
        | VRange values -> not (List.isEmpty values)
        | VMap m -> not (Map.isEmpty m.Value)
        | VJUnitSummary _ -> true
        | VClosure _
        | VFunc _ -> true

module Env =

    let empty = { Vars = Map.empty; Funcs = Map.empty }

    /// FG-179. A NEW BINDING in a NEW cell — `def x = …`, a closure or loop parameter, a
    /// catch variable. Shadowing stays lexical: the enclosing scope keeps its own cell and
    /// its own map, so a `def` inside a closure is invisible outside it.
    ///
    /// This is NOT the path for `x = …` where `x` is already a local. That must write
    /// THROUGH the existing cell (see `assign`), and routing it here instead is precisely
    /// the defect: it minted a fresh cell in a fresh map that only the assigning scope
    /// held.
    let withVar name value env =
        { env with Vars = Map.add name (ref value) env.Vars }

    /// FG-179. Write through an existing local's cell, so every scope sharing it — the
    /// closure that captured it and the scope that created it — observes the write.
    ///
    /// Returns whether the name WAS a local. `false` means the caller must fall through to
    /// the script binding, which is a different storage class with different visibility
    /// rules, and conflating the two is what the `Vars`/`Binding` split already exists to
    /// prevent.
    let assign name value env =
        match Map.tryFind name env.Vars with
        | Some cell ->
            cell.Value <- value
            true
        | None -> false

    /// The current value of every local, as values rather than cells. For a caller that
    /// wants a SNAPSHOT — one that must not observe later writes — rather than the live
    /// scope.
    let snapshot env =
        env.Vars |> Map.map (fun _ cell -> cell.Value)

    /// FG-179. Build a scope from plain values — for a CALLER that has a value map and
    /// no scope of its own to share: the walker seeding `when` and `script` from the
    /// stage environment. Each name gets its own fresh cell, so nothing outside the
    /// interpreter can observe a write through it.
    let ofValues (vars: Map<string, Value>) =
        { Vars = vars |> Map.map (fun _ value -> ref value)
          Funcs = Map.empty }

    /// FG-195. APPENDS a candidate rather than replacing the slot — overloads are real.
    /// A structurally identical candidate is skipped, NOT appended: function hoisting
    /// folds every top-level `def f()` into the env before execution and the execution
    /// pass then visits the same statement again, and under append-always that made
    /// every helper its own same-arity duplicate — ambiguous at its first call.
    ///
    /// THE SKIP HAS A COST, stated: an IN-SCRIPT byte-identical duplicate declaration
    /// collapses to one candidate and its call RUNS, where Groovy compile-rejects any
    /// duplicate signature. Narrow — any in-script `def f()` is already in a
    /// Jenkins-rejected class, and the preamble path refuses all same-arity
    /// duplicates upstream on the raw list — but it is a fact about this function,
    /// not hygiene.
    let withFunc name ps body env =
        let existing = defaultArg (Map.tryFind name env.Funcs) []

        if existing |> List.exists (fun c -> c = (ps, body)) then
            env
        else
            { env with Funcs = Map.add name (existing @ [ ps, body ]) env.Funcs }
