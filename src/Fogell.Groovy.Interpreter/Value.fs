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
    /// kept the OLD map — so the mutation existed only inside the closure and vanished
    /// when it returned.
    ///
    /// TEN MEASURED FINDINGS came from that one gap, and the shape they share is that each
    /// looked like a different bug: `def marker = 'before'; dir('sub') { marker = 'after' }`
    /// printed `before`; `[1].each { armed = true }` failed the build outright; a
    /// body-local `def env` poisoned a sibling wrapper; a closure passed BY VALUE lost its
    /// captured parameter entirely. Two independent reviewers, working from different
    /// instances, arrived at this same fix. Receipt `script-closure-mutates-enclosing`
    /// holds the hosted-body case, whose previous behaviour was a GREEN BUILD writing
    /// `marker=before` — a false success, not a crash.
    ///
    /// THE ORDINARY CLOSURE PATH IS FIXED BY THIS TOO, and the correction matters more
    /// than the fact. `def a = false; [1].each { a = true }` was reported by review as a
    /// SEPARATE defect — "applyClosure executes its block in an immutable Env and discards
    /// the returned environment" — and I recorded it as one. Measured after the cells
    /// landed, it still failed, so the separate-defect story looked confirmed.
    ///
    /// IT WAS FG-187, AN UNRELATED PARSER BUG. Written across newlines, `def a = false`
    /// followed by a line starting with `[` parses as ONE expression, `false[1]`, because
    /// the postfix index continues over the line break; `each` then has a non-list receiver
    /// and faults. With a semicolon the confound disappears and the closure path is
    /// correct: `a:[true]`, and `def n = 0; [1,2].each { n = n + 1 }` gives `n:[2]` — the
    /// counter proving the closure wrote THROUGH the variable on both iterations. Receipt
    /// `script-closure-mutates-ordinary`.
    ///
    /// TWO OBSERVERS AGREED ON A WRONG CAUSE because the symptom was real and the first
    /// plausible mechanism fit it. What separated them was removing one variable from the
    /// probe, which is the cheapest test available and was not run for months.
    { Vars: Map<string, Value ref>
      Funcs: Map<string, string list * Stmt list> }

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

    let withFunc name ps body env =
        { env with Funcs = Map.add name (ps, body) env.Funcs }
