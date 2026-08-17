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
    | VList of Value list
    | VMap of Map<string, Value>
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

    let rec toDisplay =
        function
        | VNull -> "null"
        | VBool b -> if b then "true" else "false"
        | VInt i -> string i
        | VStr s -> s
        | VList xs -> "[" + (xs |> List.map toDisplay |> String.concat ", ") + "]"
        | VMap m ->
            "["
            + (m |> Map.toList |> List.map (fun (k, v) -> $"{k}:{toDisplay v}") |> String.concat ", ")
            + "]"
        | VClosure _ -> "<closure>"
        | VFunc(n, _, _) -> $"<function {n}>"

    /// Groovy truthiness: null, false, 0, "" and empty collections are falsy.
    let isTruthy =
        function
        | VNull -> false
        | VBool b -> b
        | VInt i -> i <> 0L
        | VStr s -> s <> ""
        | VList xs -> not (List.isEmpty xs)
        | VMap m -> not (Map.isEmpty m)
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
