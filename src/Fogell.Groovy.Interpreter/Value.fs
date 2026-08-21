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
    /// FG-193. A Groovy map is a REFERENCE object: aliases see each other's
    /// mutations. The ref is the identity, exactly as ref cells are for locals —
    /// measured: `def other = local; other.FOO = 'x'` printed alias:x on Jenkins
    /// and alias:null here while the write vanished into a dropped match arm.
    | VMap of Map<string, Value> ref
    | VClosure of Closure * Env
    | VFunc of name: string * parameters: string list * body: Stmt list

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

module Value =

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
            | VClosure _ -> "<closure>"
            | VFunc(n, _, _) -> $"<function {n}>"

        let rendered = displayWith [] v
        if cycle then DisplayCycleDetected else Text rendered

    let toDisplay v =
        match tryToDisplay v with
        | Text rendered -> rendered
        | DisplayCycleDetected -> "<cyclic collection>"

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
    ///   - everything else keeps structural equality, which cannot recurse into
    ///     a cycle: only a map ref is mutable enough to close one.
    let tryEq (a: Value) (b: Value) : Compared =
        let mutable cycle = false

        let rec go (seen: (obj * obj) list) a b =
            match a, b with
            | VMap ma, VMap mb ->
                if System.Object.ReferenceEquals(ma, mb) then
                    true
                elif
                    seen
                    |> List.exists (fun (x, y) ->
                        System.Object.ReferenceEquals(x, ma) && System.Object.ReferenceEquals(y, mb))
                then
                    cycle <- true
                    false
                else
                    let inner = (box ma, box mb) :: seen

                    ma.Value.Count = mb.Value.Count
                    && ma.Value
                       |> Map.forall (fun k v ->
                           match Map.tryFind k mb.Value with
                           | Some w -> go inner v w
                           | None -> false)
            | VList xa, VList xb ->
                if System.Object.ReferenceEquals(xa, xb) then
                    true
                elif
                    seen
                    |> List.exists (fun (x, y) ->
                        System.Object.ReferenceEquals(x, xa) && System.Object.ReferenceEquals(y, xb))
                then
                    cycle <- true
                    false
                else
                    let inner = (box xa, box xb) :: seen
                    xa.Value.Length = xb.Value.Length && List.forall2 (go inner) xa.Value xb.Value
            | VClosure(c1, e1), VClosure(c2, e2) ->
                System.Object.ReferenceEquals(c1, c2) && System.Object.ReferenceEquals(e1, e2)
            | VClosure _, _
            | _, VClosure _
            | VMap _, _
            | _, VMap _
            | VList _, _
            | _, VList _ -> false
            | _ -> a = b

        let answer = go [] a b
        if cycle then CycleDetected else Answer answer

    /// Groovy truthiness: null, false, 0, "" and empty collections are falsy.
    let isTruthy =
        function
        | VNull -> false
        | VBool b -> b
        | VInt i -> i <> 0L
        | VStr s -> s <> ""
        | VList xs -> not (List.isEmpty xs.Value)
        | VMap m -> not (Map.isEmpty m.Value)
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
