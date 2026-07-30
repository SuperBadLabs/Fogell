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
    { Vars: Map<string, Value>
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

    let withVar name value env =
        { env with Vars = Map.add name value env.Vars }

    let withFunc name ps body env =
        { env with Funcs = Map.add name (ps, body) env.Funcs }
